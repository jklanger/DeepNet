﻿#load "../../../../DeepNet.fsx"
#I "../../bin/Debug"
#r "GPAct.exe"

open Basics
open Models
open SymTensor
open Datasets
open Optimizers
open GPAct


let nHidden1 = SizeSpec.fix 10
let nGPs1    = SizeSpec.fix 1
let nHidden2 = SizeSpec.fix 10
let nGPs2    = SizeSpec.fix 1


let cfg = {

    Model = {Layers = [
                       GPActivationLayer 
                        {WeightTransform = {WeightTransform.defaultHyperPars with
                                             NInput                = ConfigLoader.NInput()
                                             NOutput               = nHidden1
                                             Trainable             = true
                                             WeightsInit           = FanOptimal
                                             BiasInit              = Const 0.0f}
                         Activation      = {GPActivation.defaultHyperPars with
                                             NGPs                  = nGPs1
                                             NOutput               = nHidden1
                                             NTrnSmpls             = SizeSpec.fix 10
                                             LengthscalesTrainable = true
                                             CutOutsideRange       = true
                                             TrnXTrainable         = false
                                             TrnTTrainable         = true
                                             TrnSigmaTrainable     = false
                                             LengthscalesInit      = Const 0.4f
                                             TrnXInit              = Linspaced (-2.0f, 2.0f)
                                             TrnTInit              = Linspaced (-1.0f, 1.0f)
                                             TrnSigmaInit          = Const (sqrt 0.01f)}}

                       GPActivationLayer 
                        {WeightTransform = {WeightTransform.defaultHyperPars with
                                             NInput                = nHidden1
                                             NOutput               = nHidden2
                                             Trainable             = true
                                             WeightsInit           = FanOptimal
                                             BiasInit              = Const 0.0f}
                         Activation      = {GPActivation.defaultHyperPars with
                                             NGPs                  = nGPs2
                                             NOutput               = nHidden2
                                             NTrnSmpls             = SizeSpec.fix 10
                                             LengthscalesTrainable = true
                                             TrnXTrainable         = false
                                             CutOutsideRange       = true
                                             TrnTTrainable         = true
                                             TrnSigmaTrainable     = false
                                             LengthscalesInit      = Const 0.4f
                                             TrnXInit              = Linspaced (-2.0f, 2.0f)
                                             TrnTInit              = Linspaced (-1.0f, 1.0f)
                                             TrnSigmaInit          = Const (sqrt 0.01f)}}
                       
                       NeuralLayer
                         {NeuralLayer.defaultHyperPars with 
                              NInput        = nHidden2
                              NOutput       = ConfigLoader.NOutput()
                              TransferFunc  = NeuralLayer.SoftMax
                              WeightsTrainable = true
                              BiasTrainable = true}
                      ]
             Loss   = LossLayer.CrossEntropy}

    //dataset from https://archive.ics.uci.edu/ml/machine-learning-databases/letter-recognition/letter-recognition.data
    Data = {Path       = "../../../../Data/UCI/abalone.txt"
            Parameters = {CsvLoader.DefaultParameters with
                           TargetCols       = [0]
                           IntTreatment     = CsvLoader.IntAsNumerical
                           CategoryEncoding = CsvLoader.OneHot
                           Missing          = CsvLoader.SkipRow}}        
                                            
    Optimizer = Adam Adam.DefaultCfg

    Training = {Train.defaultCfg with 
                 MinIters  = Some 5000
                 BatchSize = System.Int32.MaxValue
                 MaxIters  = None}

    SaveParsDuringTraining = false
    PlotGPsDuringTraining  = true
}

