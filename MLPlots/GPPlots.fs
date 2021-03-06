﻿namespace MLPlots

open SymTensor.Compiler.Cuda
open RProvider.graphics
open Models
open ArrayNDNS
open SymTensor
open RProvider
open RTools

module GPPlots =
    
    /// Type of plot configurations for Gaussian Processes.
    /// If two PlotCfgs are different the plot can not be 
    /// performed by the same Gaussian Process with different parameters.
    type PlotCfg = 
        {HyperPars: GaussianProcess.HyperPars;
         NTrain:    int;
         NTest:     int}  
    
    /// Map containing initialized Gaussian Process models.
    let mutable models = Map.empty

    /// Map containing the config type of Gaussian Process models
    /// with same key the initialized model has in Map models.
    let mutable modelConfigs = Map.empty


    type Plots = 
        

        /// Checks if a fitting initialized model for a certain plot is already contained in map modles.
        /// If not creates and initializes new model and saves config in configs.
        static member getModel (config:PlotCfg) =
        
            let size = modelConfigs.Count
            let key = Map.tryFindKey (fun key value -> value = config) modelConfigs
            match key with
            | Some idx ->
                models.[idx]
            |None ->
                let mb = ModelBuilder<single> "GP"
                let nTrnSmpls = mb.Size "nTrnSmpls"
                let nInput = mb.Size "nInput"
                let sigNs = mb.Var<single> "sigs" [nTrnSmpls]
                let x = mb.Var<single>  "x" [nTrnSmpls]
                let t = mb.Var<single>  "t" [nTrnSmpls]
                let inp = mb.Var<single>  "inp" [nInput]
                let pars = GaussianProcess.pars (mb.Module "GaussianProcess") config.HyperPars
                let mi = mb.Instantiate (DevCuda,
                                         Map[nTrnSmpls, config.NTrain
                                             nInput,    config.NTest])
                let newIdx = size + 1
                modelConfigs <- modelConfigs.Add (newIdx, config)
                models <- models.Add (newIdx,(pars, x, t, sigNs, inp ,mi))
                pars, x, t, sigNs, inp ,mi
               

        /// Creates num samples from in range minValue to maxValue with constant distance.
        /// Calculates mean covariance and standerdDeviation of these samples given a Gaussian process
        /// with hyper parameters hyperPars, training noise sigmaN train values trnX and train targets trnT.
        static member predictGP hyperPars (sigmaN: ArrayNDT<single>) (trnX: ArrayNDT<single>) 
                (trnT: ArrayNDT<single>) (minValue, maxValue) nPoints =
            let config = {HyperPars = hyperPars
                          NTrain = trnX.NElems
                          NTest = nPoints}
            let pars, x, t, sigNs, inp ,mi = Plots.getModel config

            match pars, hyperPars.Kernel with
            | GaussianProcess.SEPars parsSE, GaussianProcess.SquaredExponential (l,s) ->
                mi.ParameterStorage.[parsSE.Lengthscale] <-ArrayNDHost.scalar l
                mi.ParameterStorage.[parsSE.SignalVariance] <- ArrayNDHost.scalar s
            | _ -> ()
            let mean, cov = GaussianProcess.predict pars x t sigNs inp      
            let stdev = cov |> Expr.diag |> Expr.sqrtt
        
            let meanCovStdFn = mi.Func (mean, cov, stdev) |> arg4 x t sigNs inp
        
            let sX = ArrayNDHost.linSpaced minValue maxValue nPoints |> ArrayNDCuda.toDev
            let sMean, sCov, sStd = meanCovStdFn trnX trnT sigmaN sX
            sX, sMean, sCov, sStd


        /// Plots a Gaussian Process with hyper parameters hyperpars, training noise trnSigma,
        /// train values trnX and train targets trnT.
        /// Step is the distance between two sample, smaller step => higher plot smoothness and accuraccy,
        /// longer plot creation. 
        static member simplePlot (hyperPars, trnSigma: ArrayNDT<single>, trnX: ArrayNDT<single>, trnT: ArrayNDT<single>, 
                                  ?nPoints, ?minX, ?maxX, ?minY, ?maxY) =
        
            let nPoints = defaultArg nPoints 20
            let trnDist = ArrayND.max trnX - ArrayND.min trnX |> ArrayND.value
            let minValue = defaultArg minX ((trnX |> ArrayND.min |> ArrayND.value) - trnDist * 0.1f)
            let maxValue = defaultArg maxX ((trnX |> ArrayND.max |> ArrayND.value) + trnDist * 0.1f)

            let sX, sMean, sCov, sStd = Plots.predictGP hyperPars trnSigma trnX trnT (minValue, maxValue) nPoints
        
            let sX = sX |> toFloatList 
            let sMean = sMean |> toFloatList 
            let sStd = sStd |> toFloatList 
            let trainX = trnX |> toFloatList
            let trainT = trnT |> toFloatList
            let upperStdev = List.map2 (fun m s-> m + s) sMean sStd |> List.rev
            let lowerStdev = List.map2 (fun m s-> m - s) sMean sStd
            let revX = List.rev sX

            let minY = defaultArg minY (List.min lowerStdev - abs (List.average lowerStdev) |> single) |> float
            let maxY = defaultArg maxY (List.max upperStdev + abs (List.average lowerStdev) |> single) |> float
        
            R.lock (fun () ->
                namedParams [   
                     "x", box sX
                     "y", box sMean
                     "ylim", box [minY; maxY]
                     "col", box "red"
                     "type", box "n"
                     "xlab", box "x"
                     "ylab", box "y"]
                |> R.plot |> ignore
                namedParams [   
                     "x", box (sX @ revX)
                     "y", box (lowerStdev @ upperStdev)
                     "col", box "beige"
                     "border" , box "NA"]
                |> R.polygon |>ignore
                namedParams [ 
                    "x", box sX
                    "y", box sMean
                    "col", box "black"
                    "type", box "l"
                    "size", box 2]
                |> R.lines |>ignore
                namedParams [ 
                    "x", box trainX
                    "y", box trainT
                    "col", box "red"
                    "type", box "p"
                    "size", box 2]
                |> R.lines |>ignore
            )

            ()

