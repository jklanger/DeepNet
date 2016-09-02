﻿namespace SymTensor.Compiler.Cuda

open System

open Basics
open Basics.Cuda
open ArrayNDNS
open SymTensor
open SymTensor.Compiler
open UExprTypes


[<AutoOpen>]
module CudaExecUnitTypes =

    /// A custom CUDA execution item.
    type ICudaExecItem =
        /// Asynchronously execute the item on the specified CUDA stream.
        abstract Execute: CudaExecEnvT -> StreamT -> unit

    /// a CUDA operation that will be assigned to and executed in a CUDA stream
    type CudaExecItemT =
        // memory operations
        | MemcpyDtoD of IDevMemRngTmpl * IDevMemRngTmpl
        | MemcpyHtoD of IHostMemRngTmpl * IDevMemRngTmpl
        | MemcpyDtoH of IDevMemRngTmpl * IHostMemRngTmpl
        | Memset of single * IDevMemRngTmpl
        // execution control
        | LaunchKernel of TmplInstT * WorkDimT * (ICudaArgTmpl list)
        | CallCFunc of TmplInstT * System.Type * (ICudaArgTmpl list)
        // CUBLAS calls 
        | BlasGemm of BlasTransposeOpT * BlasTransposeOpT *  
                      single * BlasTransposedMatrixTmpl * BlasTransposedMatrixTmpl * 
                      single * BlasTransposedMatrixTmpl
        | BlasGemmBatched of BlasTransposeOpT * BlasTransposeOpT *  
                             single * BlasTransposedMatrixBatchTmpl * BlasTransposedMatrixBatchTmpl * 
                             single * BlasTransposedMatrixBatchTmpl
        // LAPACK calls
        | BlasGetrfBatched of BlasTransposedMatrixBatchTmpl * 
                              BlasIntArrayTmpl * BlasIntArrayTmpl
        | BlasGetriBatched of BlasTransposedMatrixBatchTmpl * BlasIntArrayTmpl *
                              BlasTransposedMatrixBatchTmpl * BlasIntArrayTmpl                            
        // pointer array creation for CUBLAS batch calls
        | BlasInitPointerArray of BlasTransposedMatrixBatchTmpl
        // extension item
        | ExtensionExecItem of ICudaExecItem
        // misc
        | Trace of UExprT * ArrayNDManikinT


    type SrcReqsHelpersT = {
        /// Creates a channel request for the default channel.
        DfltChReq:               ArrayNDManikinT option -> ChannelReqsT
        /// The view request for the default channel of the target.
        TrgtDfltChReq:           ArrayNDManikinT option
        /// Requests the default channel of all sources without
        /// a storage requests.
        DfltSrcWithNoViewReq:    ChannelReqsT list
        /// Requests the default channel of the first source to be evaluated 
        /// into our requested target view of the the default channel.
        InplaceFirstSrcReq:      ChannelReqsT list
    }

    type TrgtGivenSrcsHelpersT = {
        /// Default channels of all sources.
        SrcsDfltCh:                         ArrayNDManikinT list
        /// Default channel is shared for all sources?
        SrcsDfltChShared:                   bool list   
        /// The view request for the default channel of the target.
        TrgtDefChReq:                       ArrayNDManikinT option
        /// Target for default channel.
        DfltChTrgt:                         ArrayNDManikinT -> bool -> ChannelManikinsAndSharedT
        // New allocated target for default channel.
        NewDfltChTrgt:                      unit -> ChannelManikinsAndSharedT
        /// default channel target that shares no elements with any srcView 
        DfltChOutplaceTrgt:                 unit -> ChannelManikinsAndSharedT     
        /// default channel target that shares no elements with any srcView 
        /// and can be used for BLAS   
        DfltChOutplaceBlasTrgt:             unit -> ChannelManikinsAndSharedT
        /// default channel target that shares no elements with any srcView 
        /// and the transpose of which can be used for BLAS
        DfltChOutplaceTransposedBlasTrgt:   unit -> ChannelManikinsAndSharedT
        /// Default channel target that reuses the default channel of a srcView, 
        /// if it may be overwritten. Otherwise uses DfltChOutplaceTrgt.
        DfltChInplaceOvrwrtTrgt:            unit -> ChannelManikinsAndSharedT
    }

    type ExecItemsHelpersT = {
        /// Default channels of all sources.
        SrcsDfltCh:                         ArrayNDManikinT list
        /// Default channel is shared for all sources?
        SrcsDfltChShared:                   bool list 
        /// Target for default channel.
        DfltChTrgt:                         ArrayNDManikinT           
        // Set pointer array values either during initialization (for allocated arrays)
        // or runtime (for variable arrays).
        AppendPointerArrayItems:            BlasTransposedMatrixBatchTmpl -> 
                                            CudaExecItemT list -> CudaExecItemT list
    }

    /// A unified op that can be compiled to CUDA execution items.
    type ICudaUOp =
        inherit IUOp

        /// Computes desired source manikins given desired target manikin.
        /// There is no guarantee that the desired source manikins will be used.
        /// Also, it is not obligatory to use the requested target manikin.
        abstract SrcReqs: CudaCompileEnvT -> SrcReqsArgs -> SrcReqsHelpersT -> 
                          ChannelReqsT list

        /// Computes the definitive target manikin of an op given its source manikins.
        /// It is not obligatory to use the requested target manikin.
        abstract TrgtGivenSrcs: CudaCompileEnvT -> TrgtGivenSrcsArgs -> 
                                TrgtGivenSrcsHelpersT -> ChannelManikinsAndSharedT
    
        /// Returns the execution items for the op.
        /// It must read from the given source manikin and write to the target manikin.
        /// Additional memory may be allocated for temporary results.
        abstract ExecItems: CudaCompileEnvT -> ExecItemsForOpArgs<CudaExecItemT> -> 
                            ExecItemsHelpersT -> CudaExecItemT list


module CudaExecUnit =
    open ManagedCuda.BasicTypes

    /// converts a IUOp to a ICudaUOp
    let toCudaUOp (uop: IUOp)  =
        match uop with
        | :? ICudaUOp as cudaUOp -> cudaUOp
        | _ -> failwith "the UOp %A needs to implement the ICudaUOp interface"

    /// The operation the blasArg will perform.
    type BlasArgOperation =
        /// no operation
        | BlasArgId
        /// in-place transposition
        | BlasArgTranspose
        /// copy into temporary array of row-major layot
        /// (no transposition occurs)
        | BlasArgCopy

    /// Returns the operation that blasArg will perform.
    let blasArgOperation (manikin: ArrayNDManikinT) shared willOverwrite =
        let st = ArrayND.stride manikin
        match st.[st.Length-2 ..] with
        | [_; 1] when not (shared && willOverwrite) -> BlasArgId
        | [1; _] when not (shared && willOverwrite) -> BlasArgTranspose
        | _ -> BlasArgCopy


    /// Computes desired source views given desired target view.
    /// There is no guarantee that the desired source views will be used.
    let srcReqs cudaEnv ({TargetShape=trgtShape
                          TargetRequest=reqChViews
                          Op=op
                          SrcShapes=srcShapes} as args) : ChannelReqsT list =
        let nSrcs = List.length srcShapes

        /// Creates a channel request for the default channel.
        let dfltChReq view : ChannelReqsT = Map [dfltChId, view] 

        /// The view request for the default channel of the target.
        let trgtDfltChReq = reqChViews.[dfltChId]

        /// Requests the default channel of all sources without
        /// a storage requests.
        let dfltSrcWithNoViewReq = List.replicate nSrcs (dfltChReq None)

        /// Requests the default channel of the first source to be evaluated 
        /// into our requested target view of the the default channel.
        let inplaceFirstSrcReq =
            match nSrcs with
            | 0 -> []
            | 1 -> [dfltChReq trgtDfltChReq]
            | _ -> dfltChReq trgtDfltChReq :: List.replicate (nSrcs-1) (dfltChReq None)

        let helpers = {
            DfltChReq               = dfltChReq
            TrgtDfltChReq           = trgtDfltChReq
            DfltSrcWithNoViewReq    = dfltSrcWithNoViewReq
            InplaceFirstSrcReq      = inplaceFirstSrcReq
        }

        match op with
        | ULeafOp _ -> []

        // unary element-wise
        | UUnaryOp Negate -> inplaceFirstSrcReq                        
        | UUnaryOp Abs -> inplaceFirstSrcReq
        | UUnaryOp SignT -> inplaceFirstSrcReq
        | UUnaryOp Log -> inplaceFirstSrcReq
        | UUnaryOp Log10 -> inplaceFirstSrcReq                           
        | UUnaryOp Exp -> inplaceFirstSrcReq                           
        | UUnaryOp Sin -> inplaceFirstSrcReq
        | UUnaryOp Cos -> inplaceFirstSrcReq
        | UUnaryOp Tan -> inplaceFirstSrcReq
        | UUnaryOp Asin -> inplaceFirstSrcReq
        | UUnaryOp Acos -> inplaceFirstSrcReq
        | UUnaryOp Atan -> inplaceFirstSrcReq
        | UUnaryOp Sinh -> inplaceFirstSrcReq
        | UUnaryOp Cosh -> inplaceFirstSrcReq
        | UUnaryOp Tanh -> inplaceFirstSrcReq
        | UUnaryOp Sqrt -> inplaceFirstSrcReq
        | UUnaryOp Ceil -> inplaceFirstSrcReq
        | UUnaryOp Floor -> inplaceFirstSrcReq
        | UUnaryOp Round -> inplaceFirstSrcReq
        | UUnaryOp Truncate -> inplaceFirstSrcReq      
        // tensor ops
        | UUnaryOp (Diag _) -> dfltSrcWithNoViewReq
        | UUnaryOp (DiagMat _) -> dfltSrcWithNoViewReq
        | UUnaryOp Invert -> dfltSrcWithNoViewReq          
        // reductions
        | UUnaryOp Sum -> dfltSrcWithNoViewReq
        | UUnaryOp (SumAxis _) -> dfltSrcWithNoViewReq
        // shape operations
        | UUnaryOp (Reshape _) ->        
            match trgtDfltChReq with
            | Some rv when ArrayND.isC rv ->
                [dfltChReq (Some (ArrayND.reshapeView srcShapes.[0] rv))]
            | _ -> dfltSrcWithNoViewReq
        | UUnaryOp (DoBroadcast _) -> dfltSrcWithNoViewReq
        | UUnaryOp (SwapDim (ax1, ax2)) ->
            match trgtDfltChReq with
            | Some rv -> [dfltChReq (Some (ArrayND.swapDim ax1 ax2 rv))]
            | _ -> dfltSrcWithNoViewReq

        // variable access
        | UUnaryOp (StoreToVar vs) ->
            match cudaEnv.VarStorLoc |> Map.find vs with
            | LocDev -> 
                // request to store directly into external var
                // we assume that all device input vars are continguous
                [dfltChReq (Some (ArrayNDManikin.externalC (MemExternal vs) srcShapes.[0]))]
            | LocHost -> dfltSrcWithNoViewReq
            | loc -> unsupLoc loc
        // misc
        | UUnaryOp (Annotated _) -> inplaceFirstSrcReq

        // binary element-wise
        | UBinaryOp Add -> inplaceFirstSrcReq
        | UBinaryOp Substract -> inplaceFirstSrcReq
        | UBinaryOp Multiply -> inplaceFirstSrcReq
        | UBinaryOp Divide -> inplaceFirstSrcReq
        | UBinaryOp Modulo -> inplaceFirstSrcReq
        | UBinaryOp Power -> inplaceFirstSrcReq
        // matrix/tensor operations
        | UBinaryOp Dot -> dfltSrcWithNoViewReq
        | UBinaryOp TensorProduct -> dfltSrcWithNoViewReq     

        // nary
        | UNaryOp Discard -> dfltSrcWithNoViewReq
        | UNaryOp (Subtensor _) -> dfltSrcWithNoViewReq
        | UNaryOp (SetSubtensor _) -> 
            // "a" can be evaluated into requested manikin, but "b" (the replacement value) must be placed
            // in a temporary manikin and copied over to avoid race conditions.
            inplaceFirstSrcReq
        | UNaryOp (Elements _) -> dfltSrcWithNoViewReq            
        | UNaryOp (ExtensionOp eop) -> (toCudaUOp eop).SrcReqs cudaEnv args helpers


    /// computes the definitive target view of an op given its source views
    let trgtGivenSrcs compileEnv ({MemAllocator=memAllocator
                                   TargetRequest=reqChViews
                                   Op=op
                                   Metadata={TargetType=typ
                                             TargetNShape=trgtShape}
                                   Srcs=srcs} as args) =

        /// Default channels of all sources.
        let srcsDfltCh, srcsDfltChShared =
            srcs
            |> List.map (fun srcChs -> srcChs.[dfltChId])
            |> List.unzip

        /// The view request for the default channel of the target.
        let trgtDefChReq = reqChViews.[dfltChId]

        /// Target for default channel.
        let dfltChTrgt view shared : ChannelManikinsAndSharedT =
            Map [dfltChId, (view, shared)] 

        // New allocated target for default channel.
        let newDfltChTrgt () = 
            dfltChTrgt (ArrayNDManikin.newC memAllocator typ trgtShape) false        

        /// True if specified manikin overlaps with any channel of any source.
        let overlappingWithAnySrc (rv: ArrayNDManikinT) =
            srcs
            |> List.exists (Map.exists (fun ch (view, shared) -> ArrayND.overlapping rv view))

        /// default channel target that shares no elements with any srcView 
        let dfltChOutplaceTrgt () =
            match trgtDefChReq with
            | Some rv when not (overlappingWithAnySrc rv) -> dfltChTrgt rv false
            | _ -> newDfltChTrgt () 
             
        /// default channel target that shares no elements with any srcView and can be used for BLAS
        let dfltChOutplaceBlasTrgt () = 
            match trgtDefChReq with
            | Some rv when ArrayNDManikin.canBeBlasTarget rv && 
                           not (overlappingWithAnySrc rv) -> dfltChTrgt rv false
            | _ -> 
                dfltChTrgt (ArrayNDManikin.newBlasTarget memAllocator typ trgtShape) false

        /// default channel target that shares no elements with any srcView and the transpose of which can be used for BLAS
        let dfltChOutplaceTransposedBlasTrgt () = 
            match trgtDefChReq with
            | Some rv when ArrayNDManikin.canBeBlasTarget rv.T && 
                           not (overlappingWithAnySrc rv) -> dfltChTrgt rv false
            | _ -> 
                dfltChTrgt (ArrayNDManikin.newC memAllocator typ trgtShape) false  

        /// Default channel target that reuses the default channel of a srcView, 
        /// if it may be overwritten. Otherwise uses defaultChOutplaceTrgt.
        let dfltChInplaceOvrwrtTrgt () : ChannelManikinsAndSharedT =
            match srcs 
                  |> List.tryFind (fun srcChs ->
                                    let view, shared = srcChs.[dfltChId] 
                                    not (ArrayND.isBroadcasted view) && not shared) with
            | Some srcChs -> Map [dfltChId, srcChs.[dfltChId]]
            | None -> dfltChOutplaceTrgt ()     

        let helpers = {
            SrcsDfltCh                          = srcsDfltCh
            SrcsDfltChShared                    = srcsDfltChShared
            TrgtDefChReq                        = trgtDefChReq
            DfltChTrgt                          = dfltChTrgt
            NewDfltChTrgt                       = newDfltChTrgt
            DfltChOutplaceTrgt                  = dfltChOutplaceTrgt
            DfltChOutplaceBlasTrgt              = dfltChOutplaceBlasTrgt
            DfltChOutplaceTransposedBlasTrgt    = dfltChOutplaceTransposedBlasTrgt
            DfltChInplaceOvrwrtTrgt             = dfltChInplaceOvrwrtTrgt
        }

        match op with
        // variable access
        | ULeafOp (Var vs) ->       
            match compileEnv.VarStorLoc |> Map.find vs with
            | LocDev ->
                // we assume that all device input vars are contiguous
                dfltChTrgt (ArrayNDManikin.externalC (MemExternal vs) trgtShape) true
            | LocHost ->
                // will transfer variable from host to device during execution
                // need contiguous memory for that
                match trgtDefChReq with
                | Some rv when ArrayND.isC rv -> dfltChTrgt rv false
                | _ -> dfltChTrgt (ArrayNDManikin.newC memAllocator typ trgtShape) false    
            | loc -> unsupLoc loc                    
        // tensor creation
        | ULeafOp _ -> dfltChOutplaceTrgt ()      

        // unary element-wise
        | UUnaryOp Negate -> dfltChInplaceOvrwrtTrgt ()                       
        | UUnaryOp Abs -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp SignT -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Log -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Log10 -> dfltChInplaceOvrwrtTrgt ()                          
        | UUnaryOp Exp -> dfltChInplaceOvrwrtTrgt ()                           
        | UUnaryOp Sin -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Cos -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Tan -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Asin -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Acos -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Atan -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Sinh -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Cosh -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Tanh -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Sqrt -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Ceil -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Floor -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Round -> dfltChInplaceOvrwrtTrgt ()
        | UUnaryOp Truncate -> dfltChInplaceOvrwrtTrgt ()    
        // tensor ops
        | UUnaryOp (Diag (ax1, ax2)) ->
            dfltChTrgt (ArrayND.diagAxis ax1 ax2 srcsDfltCh.[0]) srcsDfltChShared.[0]
        | UUnaryOp (DiagMat (ax1, ax2)) -> dfltChOutplaceTrgt ()
        | UUnaryOp Invert -> 
            // If source will be transposed, then target will also be transposed.
            // Thus, in this case, we must request an array the transpose of which 
            // can be used as a BLAS target.
            match blasArgOperation srcsDfltCh.[0] srcsDfltChShared.[0] true with
            | BlasArgTranspose -> dfltChOutplaceBlasTrgt ()
            | _ -> dfltChOutplaceTransposedBlasTrgt ()
        // reductions
        | UUnaryOp Sum -> dfltChOutplaceTrgt ()
        | UUnaryOp (SumAxis _) -> dfltChOutplaceTrgt ()
        // shape operations
        | UUnaryOp (Reshape _) ->        
            // TODO: optimize: check if copy is really necessary
            if ArrayND.isC srcsDfltCh.[0] then
                dfltChTrgt (ArrayND.reshapeView trgtShape srcsDfltCh.[0]) srcsDfltChShared.[0] 
            else dfltChOutplaceTrgt () // will copy
        | UUnaryOp (DoBroadcast _) ->
            dfltChTrgt (ArrayND.broadcastToShape trgtShape srcsDfltCh.[0]) srcsDfltChShared.[0]
        | UUnaryOp (SwapDim (ax1, ax2)) ->
            dfltChTrgt (ArrayND.swapDim ax1 ax2 srcsDfltCh.[0]) srcsDfltChShared.[0]
        // variable access
        | UUnaryOp (StoreToVar _) -> 
            // output of StoreToVar is empty 
            newDfltChTrgt ()
        // misc
        | UUnaryOp (Annotated _) -> dfltChTrgt srcsDfltCh.[0] srcsDfltChShared.[0]

        // binary element-wise
        | UBinaryOp Add -> dfltChInplaceOvrwrtTrgt ()
        | UBinaryOp Substract -> dfltChInplaceOvrwrtTrgt ()
        | UBinaryOp Multiply -> dfltChInplaceOvrwrtTrgt ()
        | UBinaryOp Divide -> dfltChInplaceOvrwrtTrgt ()
        | UBinaryOp Modulo -> dfltChInplaceOvrwrtTrgt ()
        | UBinaryOp Power -> dfltChInplaceOvrwrtTrgt ()
        // matrix/tensor operations
        | UBinaryOp Dot -> dfltChOutplaceBlasTrgt ()
        | UBinaryOp TensorProduct -> dfltChOutplaceTrgt ()

        // nary
        | UNaryOp Discard -> dfltChOutplaceTrgt ()
        | UNaryOp (Subtensor srs) -> 
            if SimpleRangesSpec.isDynamic srs then 
                // dynamic sub-tensors will be copied out of the src
                dfltChOutplaceTrgt ()
            else
                // symbolic sub-tensors use a view of the src 
                let rng = SimpleRangesSpec.eval (fun _ -> failwith "must be static") srs
                dfltChTrgt (srcsDfltCh.[0].[rng] :?> ArrayNDManikinT) srcsDfltChShared.[0]
        | UNaryOp (SetSubtensor _) ->
            if not (srcsDfltChShared.[0]) then 
                dfltChTrgt srcsDfltCh.[0] false
            else dfltChOutplaceTrgt ()
        | UNaryOp (Elements _) -> dfltChOutplaceTrgt ()
        | UNaryOp (ExtensionOp eop) -> 
            (toCudaUOp eop).TrgtGivenSrcs compileEnv args helpers
   
    /// execution item to launch the given kernel template function
    let execItemsForKernel cppFuncName tmplTmpls argTmpls workDim = 
        let cFuncTmpl = {
            FuncName=cppFuncName
            Domain=KernelFunc
            TmplArgs=List.map (fun (a: ICudaArgTmpl) -> a.CPPTypeName) tmplTmpls
            RetType="void"
            ArgTypes=List.map (fun (a: ICudaArgTmpl) -> a.CPPTypeName) argTmpls
        }    
        [LaunchKernel(cFuncTmpl, workDim, argTmpls)]

    /// returns the CUDA work dimensions for an element-wise or elements operation
    let workDimForElemwise trgt hetero =
        match ArrayND.nDims trgt with
        | _ when hetero -> (ArrayND.nElems trgt, 1, 1)
        | 0 -> (1, 1, 1)
        | 1 -> ((ArrayND.shape trgt).[0], 1, 1)
        | 2 -> ((ArrayND.shape trgt).[0], (ArrayND.shape trgt).[1], 1)
        | 3 -> ((ArrayND.shape trgt).[0], (ArrayND.shape trgt).[1], (ArrayND.shape trgt).[2])
        | d ->
            let rest = {2 .. d-1} |> Seq.map (fun i -> (ArrayND.shape trgt).[i]) |> Seq.fold (*) 1 
            ((ArrayND.shape trgt).[0], (ArrayND.shape trgt).[1], rest)

    /// returns the C++ template instantiation code for the given template and argument list
    let cppTemplateInstantiation tmpl args =
        if List.isEmpty args then tmpl
        else sprintf "%s<%s>" tmpl (args |> String.concat ", ")

    /// function name of element-wise wrapper and its arguments for the given target, operation and sources
    let elemwiseFuncnameAndArgs trgt cOp srcViews =
        let args = 
            (cOp :> ICudaArgTmpl) ::
            ((ArrayNDArgTmpl trgt) :> ICudaArgTmpl) ::
            (List.map (fun v -> (ArrayNDArgTmpl v) :> ICudaArgTmpl) srcViews)

        let nSrc = List.length srcViews
        let hetero = srcViews |> List.exists (fun sv -> (ArrayND.shape trgt) <> (ArrayND.shape sv))
        let indexedStr = if (cOp :> ICudaOp).IsIndexed then "Indexed" else ""
        let dimsStr = if hetero then "Heterogenous" else sprintf "%dD" (ArrayND.nDims trgt)
        let funcName = sprintf "elemwise%dAry%s%s" nSrc dimsStr indexedStr 
        funcName, args

    /// execution items for an element-wise operation
    let execItemsForElemwise trgt cOp srcViews =
        if srcViews |> List.exists (fun sv -> ArrayND.nElems trgt <> ArrayND.nElems sv) then
            failwithf "a source of an elemwise op has different number of elements than target"

        let funcName, args = elemwiseFuncnameAndArgs trgt cOp srcViews
        let hetero = srcViews |> List.exists (fun sv -> (ArrayND.shape trgt) <> (ArrayND.shape sv))
        execItemsForKernel funcName args args (workDimForElemwise trgt hetero)

    /// function name of elements wrapper and its arguments for the given target, operation and sources
    let elementsFuncnameAndArgs trgt cOp srcViews =
        let args = 
            (cOp :> ICudaArgTmpl) ::
            ((ArrayNDArgTmpl trgt) :> ICudaArgTmpl) ::
            (List.map (fun v -> (ArrayNDArgTmpl v) :> ICudaArgTmpl) srcViews)

        let nSrc = List.length srcViews
        let dimsStr = sprintf "%dD" (ArrayND.nDims trgt)
        let funcName = sprintf "elements%dAry%s" nSrc dimsStr 
        funcName, args

    /// execution items for an element-wise operation
    let execItemsForElements compileEnv trgt elemFunc srcViews =
        let opName = 
            match compileEnv.ElemFuncsOpNames |> Map.tryFind elemFunc with
            | Some opName -> opName
            | None ->
                let id = compileEnv.ElemFuncsOpNames |> Map.toSeq |> Seq.length
                let opName = sprintf "ElemFunc%dOp" id
                compileEnv.ElemFuncsOpNames <- compileEnv.ElemFuncsOpNames |> Map.add elemFunc opName
                opName
        let opTmplArgs = 
            srcViews
            |> List.map (fun (manikin: ArrayNDManikinT) -> manikin.CPPType)
            |> String.concat ", "
        let opTypeName = sprintf "%s<%s>" opName opTmplArgs

        let funcName, args = elementsFuncnameAndArgs trgt (ElementsOpArgTmpl opTypeName) srcViews
        let workDims = workDimForElemwise trgt false
        execItemsForKernel funcName args args workDims

    let dynamicSubtensorTmplAndIdx (bas: ArrayNDManikinT) (rngs: UExprRngsSpecT) (rngManikins: ArrayNDManikinT list) =
        // Apply symbolic ranges to src, and leave dynamic axes unharmed.
        // (0 is added to offset and their size is changed appropriately)
        let basStatic = bas.[SimpleRangesSpec.eval (fun _ -> 0) rngs] :?> ArrayNDManikinT

        // convert simplified range specification to array of pointers to expressions calculating
        // the indices
        let rec rngToIdxPntrs rngs rngManikins =
            match rngs, rngManikins with
            | SRSDynStartSymSize _ :: rrngs, rngManikin :: rrngManikins ->
                // for dynamic range pass pointer to result of expression calculating the index
                (SizeTPtrFromArrayNDIdxTmpl (Some rngManikin) :> ICudaArrayMemberArgTmpl<IntPtr>) :: 
                    rngToIdxPntrs rrngs rrngManikins 
            | SRSSymStartSymEnd _ :: rrngs, _ ->
                // symbolic range has already been applied, pass null (meaning no offset to add)
                (SizeTPtrFromArrayNDIdxTmpl None :> ICudaArrayMemberArgTmpl<IntPtr>) :: 
                    rngToIdxPntrs rrngs rngManikins 
            | [], [] -> []
            | _ -> failwith "invalid dynamic range specification"
        let basIdxPntrs = rngToIdxPntrs rngs rngManikins

        // C++ parameters
        ArrayNDArgTmpl basStatic, ArrayNDSDArgTmpl basStatic, CPPArrayTmpl basIdxPntrs

    let execItemsForCopyFromDynamicSubtensor trgt src rngs rngManikins =
        // C++ signature is:
        //template <typename TTarget, typename TBaseSrc, typename TDynSrc, size_t nDims,
        //          TElemwise1Ary<IdEOp_t, TTarget, TDynSrc>::type copyFun>
        //_dev void copyFromDynamicSubtensor(TTarget &trgt,  
        //                                   const TBaseSrc &baseSrc, const Array<size_t, nDims> &srcIdx)

        let srcTmpl, srcDynTmpl, srcIdxPntrsTmpl = dynamicSubtensorTmplAndIdx src rngs rngManikins
        let nDimsStr = sprintf "%d" (ArrayND.nDims trgt)

        execItemsForKernel 
            "copyFromDynamicSubtensor" 
            [ArrayNDArgTmpl trgt; srcTmpl; srcDynTmpl; CPPTemplateValue nDimsStr]
            [ArrayNDArgTmpl trgt; srcTmpl; srcIdxPntrsTmpl]
            (workDimForElemwise trgt false)

    let execItemsForCopyToDynamicSubtensor trgt rngs rngManikins src =
        // C++ signature is:
        //template <typename TBaseTrgt, typename TDynTrgt, size_t nDims, typename TSrc,
        //          TElemwise1Ary<IdEOp_t, TDynTrgt, TSrc>::type copyFun>
        //_dev void copyToDynamicSubtensor(TBaseTrgt &baseTrgt, const Array<size_t, nDims> &trgtIdx,
        //                                 const TSrc &src)
          
        let trgtTmpl, trgtDynTmpl, trgtIdxPntrsTmpl = dynamicSubtensorTmplAndIdx trgt rngs rngManikins
        let nDimsStr = sprintf "%d" (ArrayND.nDims src)  

        execItemsForKernel 
            "copyToDynamicSubtensor" 
            [trgtTmpl; trgtDynTmpl; CPPTemplateValue nDimsStr; ArrayNDArgTmpl src]
            [trgtTmpl; trgtIdxPntrsTmpl; ArrayNDArgTmpl src]
            (workDimForElemwise src false)


    /// generate ExecItems to call a C++ template function
    let execItemsForCFunc<'FuncDelegate when 'FuncDelegate :> System.Delegate> tmplTmpls argTmpls =
        let cDelegateType = typeof<'FuncDelegate>
        let cAttributes = cDelegateType.GetCustomAttributes(typeof<CPPFuncNameAttribute>, false)
        if Array.isEmpty cAttributes then
            failwithf "CPPFuncName attribute is missing on delegate %A" cDelegateType
        let cppFuncNameAttribute = cAttributes.[0] :?> CPPFuncNameAttribute
        let cppFuncName = cppFuncNameAttribute.CPPFuncName

        let cFuncTmpl = {
            FuncName=cppFuncName
            Domain=CPPFunc
            TmplArgs=List.map (fun (a: ICudaArgTmpl) -> a.CPPTypeName) tmplTmpls
            RetType="void"
            ArgTypes=List.map (fun (a: ICudaArgTmpl) -> a.CPPTypeName) argTmpls
        }    
        [CallCFunc(cFuncTmpl, cDelegateType, argTmpls)]


    /// generates ExecItems to copy srcView to trgtView 
    let copyExecItems trgt src =
        if ArrayND.nElems trgt <> ArrayND.nElems src then
            failwithf "cannot copy array with %d elements to array with %d elements"
                (ArrayND.nElems trgt) (ArrayND.nElems src)
        execItemsForElemwise trgt (NoArgEOpArgTmpl("IdEOp_t", false)) [src]

    /// If all batch dimensions (all dimensions but the last two) of the array are of
    /// size one, a view of the last two dimensions is returned.
    /// Otherwise the original array is returned.
    let trimUnitaryBatchedBlasDims (manikin: ArrayNDManikinT) =
        let nd = manikin.NDims
        if nd > 2 then
            let isUnitary = manikin.Shape.[0..nd-3] |> List.forall ((=) 1)
            if isUnitary then
                manikin |> ArrayND.reshapeView manikin.Shape.[nd-2..]
            else manikin
        else manikin           

    /// BLAS input argument passing, so that orientation is preserved.
    /// Can return copy items if deemed necessary.
    let blasArg memAllocator (manikin: ArrayNDManikinT) shared willOverwrite =
        let manikin = trimUnitaryBatchedBlasDims manikin
        if ArrayND.nDims manikin < 2 then
            failwith "need at least 2-dimensional array for BLAS argument"
        match blasArgOperation manikin shared willOverwrite with
        | BlasArgId        -> manikin, BlasTranspose, [], shared
        | BlasArgTranspose -> ArrayND.transpose manikin, BlasId, [], shared
        | BlasArgCopy -> 
            let tmpView = ArrayNDManikin.newC memAllocator (ArrayNDManikin.typeName manikin) (ArrayND.shape manikin)
            let copyOps = copyExecItems tmpView manikin
            tmpView, BlasTranspose, copyOps, false

    /// BLAS target argument passing, so that orientation is preserved
    let blasTarget (manikin: ArrayNDManikinT) =
        let manikin = trimUnitaryBatchedBlasDims manikin
        if not (ArrayNDManikin.canBeBlasTarget manikin) then
            failwithf "cannot use specified view with shape %A and stride %A as BLAS target" 
                manikin.Shape (ArrayND.stride manikin)
        ArrayND.transpose manikin

    let execItemsForSum memAllocator trgt src =
        // C++ signature:
        // void sum(TTarget &trgt, TSrc &src, 
        //          CUstream &stream, char *tmp_buffer, size_t tmp_buffer_size);
        let tmpSize = ArrayNDManikin.sizeInBytes src
        let tmp = memAllocator TypeName.ofType<byte> tmpSize MemAllocDev       
        execItemsForCFunc<CPPSum> [] [ArrayNDArgTmpl trgt; ArrayNDArgTmpl src;
                                      ExecStreamArgTmpl(); BytePtrArgTmpl tmp; SizeTArgTmpl tmpSize]

    let execItemsForSumAxis memAllocator ax trgt src =
        // we need to swap axes so that the axes the summation is performed over comes last
        let nd = ArrayND.nDims src
        let axOrder = Seq.concat [ {0 .. ax-1}; {ax + 1 .. nd - 1}; Seq.singleton ax] |> Seq.toList
        let srcAdj = ArrayND.reorderAxes axOrder src

        // C++ signature:
        // void sumLastAxis(TTarget &trgt, TSrc &src, 
        //                  CUstream &stream, char *tmp_buffer, size_t tmp_buffer_size);
        let tmpSize = ArrayNDManikin.sizeInBytes srcAdj
        let tmp = memAllocator TypeName.ofType<byte> tmpSize MemAllocDev
        execItemsForCFunc<CPPSumLastAxis> [] [ArrayNDArgTmpl trgt; ArrayNDArgTmpl srcAdj;
                                                ExecStreamArgTmpl(); BytePtrArgTmpl tmp; SizeTArgTmpl tmpSize]

    /// returns the execution units for the specified op
    let execItemsForOp compileEnv ({MemAllocator=memAllocator
                                    Target=trgtChs
                                    Op=op
                                    Metadata=metadata
                                    Srcs=srcsAndShared
                                    SubmitInitItems=submitInit} as args) =

        /// Default channel of target.
        let dfltChTrgt = trgtChs.[dfltChId]

        /// Default channels of all sources.
        let srcsDfltCh, srcsDfltChShared =
            srcsAndShared
            |> List.map (fun srcChs -> srcChs.[dfltChId])
            |> List.unzip
    
        // set pointer array values either during initialization (for allocated arrays)
        // or runtime (for variable arrays)
        let appendPointerArrayItems (tmpl: BlasTransposedMatrixBatchTmpl) execItems =
            match tmpl.Manikin.Storage with
            | MemAlloc _ -> submitInit [BlasInitPointerArray tmpl]; execItems
            | MemExternal _ -> execItems @ [BlasInitPointerArray tmpl]

        let helpers = {
            SrcsDfltCh              = srcsDfltCh
            SrcsDfltChShared        = srcsDfltChShared
            DfltChTrgt              = dfltChTrgt
            AppendPointerArrayItems = appendPointerArrayItems
        }

        match op with 
        // tensor creation
        | ULeafOp (Identity _) -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("DiagonalOneIEOp_t", true)) []
        | ULeafOp (Zeros _) -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("ZerosEOp_t", false)) []
        | ULeafOp (ScalarConst f) -> execItemsForElemwise dfltChTrgt (ConstEOpArgTmpl f) [] 
        | ULeafOp (SizeValue sv) -> 
            let value = Convert.ChangeType(SizeSpec.eval sv, dfltChTrgt.DataType)
            let opType = typedefof<ConstEOpArgTmpl<_>>.MakeGenericType(dfltChTrgt.DataType)
            let op = Activator.CreateInstance(opType, value) :?> ICudaOpAndArgTmpl 
            execItemsForElemwise dfltChTrgt op [] 
        // variable access
        | ULeafOp (Var vs) -> 
            match compileEnv.VarStorLoc |> Map.find vs with
            | LocDev -> []
            | LocHost -> 
                // we assume that host variable has continguous stride and zero offset
                let hv = ArrayNDManikin.externalC (MemExternal vs) (ArrayND.shape dfltChTrgt)
                [MemcpyHtoD(ArrayNDHostRegMemRngTmpl(hv), ArrayNDDevMemRngTmpl(dfltChTrgt))]       
            | loc -> unsupLoc loc
        // unary element-wise
        | UUnaryOp Negate -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("NegateEOp_t", false)) srcsDfltCh
        | UUnaryOp Abs -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("AbsEOp_t", false)) srcsDfltCh
        | UUnaryOp SignT -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("SignTEOp_t", false)) srcsDfltCh
        | UUnaryOp Log -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("LogEOp_t", false)) srcsDfltCh
        | UUnaryOp Log10 -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("Log10EOp_t", false)) srcsDfltCh
        | UUnaryOp Exp -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("ExpEOp_t", false)) srcsDfltCh
        | UUnaryOp Sin -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("SinEOp_t", false)) srcsDfltCh
        | UUnaryOp Cos -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("CosEOp_t", false)) srcsDfltCh
        | UUnaryOp Tan -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("TanEOp_t", false)) srcsDfltCh
        | UUnaryOp Asin -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("AsinEOp_t", false)) srcsDfltCh
        | UUnaryOp Acos -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("AcosEOp_t", false)) srcsDfltCh
        | UUnaryOp Atan -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("AtanEOp_t", false)) srcsDfltCh
        | UUnaryOp Sinh -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("SinhEOp_t", false)) srcsDfltCh
        | UUnaryOp Cosh -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("CoshEOp_t", false)) srcsDfltCh
        | UUnaryOp Tanh -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("TanhEOp_t", false)) srcsDfltCh
        | UUnaryOp Sqrt -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("SqrtEOp_t", false)) srcsDfltCh
        | UUnaryOp Ceil -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("CeilEOp_t", false)) srcsDfltCh
        | UUnaryOp Floor -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("FloorEOp_t", false)) srcsDfltCh
        | UUnaryOp Round -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("RoundEOp_t", false)) srcsDfltCh
        | UUnaryOp Truncate -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("TruncateEOp_t", false)) srcsDfltCh
        // reductions
        | UUnaryOp Sum -> execItemsForSum memAllocator dfltChTrgt srcsDfltCh.[0]
        | UUnaryOp (SumAxis ax) -> execItemsForSumAxis memAllocator ax dfltChTrgt srcsDfltCh.[0]

        // tensor ops
        | UUnaryOp (Diag _) -> []
        | UUnaryOp (DiagMat (ax1, ax2)) ->
            let trgtDiag = ArrayND.diagAxis ax1 ax2 dfltChTrgt
            let zeroItems = execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("ZerosEOp_t", false)) []
            let copyItems = copyExecItems trgtDiag srcsDfltCh.[0]
            zeroItems @ copyItems
        | UUnaryOp Invert ->
            let aView, _, aCopyItems, _ = blasArg memAllocator srcsDfltCh.[0] srcsDfltChShared.[0] true

            let tView =
                // If the source is transposed by us then the target must be transposed by us 
                // as well to preserve orientation. The blasTarget function always transposes.
                match blasArgOperation srcsDfltCh.[0] srcsDfltChShared.[0] true with
                | BlasArgTranspose -> blasTarget dfltChTrgt
                | _ -> blasTarget (ArrayND.transpose dfltChTrgt)

            // allocate variables and initialize pointer arrays
            let aArg = BlasTransposedMatrixBatchTmpl (aView, memAllocator)
            let tArg = BlasTransposedMatrixBatchTmpl (tView, memAllocator)
            let pivot = BlasIntArrayTmpl (aArg.Rows * aArg.NSamples, memAllocator)
            let info = BlasIntArrayTmpl (aArg.NSamples, memAllocator)
            let ptrAryItems =
                []
                |> appendPointerArrayItems aArg
                |> appendPointerArrayItems tArg

            // Perform LU decomposition in-place in b.
            let luItems = [BlasGetrfBatched (aArg, pivot, info)]

            // Perform matrix inversion from b into t.
            let invItems = [BlasGetriBatched (aArg, pivot, tArg, info)]

            aCopyItems @ ptrAryItems @ luItems @ invItems

        // shape operations
        | UUnaryOp (Reshape _) ->
            if dfltChTrgt <> srcsDfltCh.[0] then 
                copyExecItems dfltChTrgt srcsDfltCh.[0]
            else []
        | UUnaryOp (DoBroadcast _) -> []
        | UUnaryOp (SwapDim _) -> []
        // variable access
        | UUnaryOp (StoreToVar vs) ->
            let varShp, varType = 
                ArrayND.shape srcsDfltCh.[0], srcsDfltCh.[0].TypeName

            match compileEnv.VarStorLoc |> Map.find vs with
            | LocDev when srcsDfltCh.[0].Storage = (MemExternal vs) ->
                // Source was evaluated directly into the variable storage.
                // No copy necessary.
                []
            | LocDev  -> 
                // Our source has not been evaluated directly into the variable storage.
                // Therefore we need to copy into the variable.
                // We assume that all device vars are continguous.
                let dv = ArrayNDManikin.externalC (MemExternal vs) varShp
                copyExecItems dv srcsDfltCh.[0]
            | LocHost ->            
                let copyItems, memcpySrc = 
                    if ArrayND.isC srcsDfltCh.[0] then 
                        // Source is contiguous. Can directly copy to host.
                        [], srcsDfltCh.[0]
                    else
                        // Need to copy to temporary contiguous storage first.
                        let tmp = ArrayNDManikin.newC memAllocator varType varShp
                        copyExecItems tmp srcsDfltCh.[0], tmp

                // We assume that all host vars are continguous.
                // trgtView has contingous stride
                let hv = ArrayNDManikin.externalC (MemExternal vs) varShp
                copyItems @ [MemcpyDtoH(ArrayNDDevMemRngTmpl(memcpySrc), ArrayNDHostRegMemRngTmpl(hv))]   
            | loc -> unsupLoc loc                              
        // misc
        | UUnaryOp (Annotated _) -> []

        // binary element-wise
        | UBinaryOp Add ->       execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("AddEOp_t",       false)) srcsDfltCh
        | UBinaryOp Substract -> execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("SubstractEOp_t", false)) srcsDfltCh
        | UBinaryOp Multiply ->  execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("MultiplyEOp_t",  false)) srcsDfltCh
        | UBinaryOp Divide ->    execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("DivideEOp_t",    false)) srcsDfltCh
        | UBinaryOp Modulo ->    execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("ModuloEOp_t",    false)) srcsDfltCh
        | UBinaryOp Power ->     execItemsForElemwise dfltChTrgt (NoArgEOpArgTmpl("PowerEOp_t",     false)) srcsDfltCh
        // matrix/tensor operations
        | UBinaryOp Dot -> 
            let aView, aOp, aCopyItems, aShared = blasArg memAllocator srcsDfltCh.[0] srcsDfltChShared.[0] false
            let bView, bOp, bCopyItems, bShared = blasArg memAllocator srcsDfltCh.[1] srcsDfltChShared.[1] false
            let tView = blasTarget dfltChTrgt
        
            let blasItems =    
                match aView.NDims with
                | 0 | 1 -> failwith "BLAS matrix must be at least two dimensional" 
                | 2 -> // single matrix multiplication
                    [BlasGemm(aOp, bOp, 1.0f, 
                              BlasTransposedMatrixTmpl(aView), 
                              BlasTransposedMatrixTmpl(bView),
                              0.0f, BlasTransposedMatrixTmpl(tView))]                
                | _ -> // batched matrix multiplication

                    // allocate memory for pointer arrays and create argument templates
                    let aTmpl = BlasTransposedMatrixBatchTmpl(aView, memAllocator)   
                    let bTmpl = BlasTransposedMatrixBatchTmpl(bView, memAllocator)   
                    let tTmpl = BlasTransposedMatrixBatchTmpl(tView, memAllocator)   

                    let execItems =
                        []
                        |> appendPointerArrayItems aTmpl
                        |> appendPointerArrayItems bTmpl
                        |> appendPointerArrayItems tTmpl

                    execItems @ [BlasGemmBatched(aOp, bOp, 1.0f, aTmpl, bTmpl, 0.0f, tTmpl)]

            aCopyItems @ bCopyItems @ blasItems

        | UBinaryOp TensorProduct -> [] // TODO

        // nary
        | UNaryOp Discard -> []
        | UNaryOp (Subtensor srs) ->
            if SimpleRangesSpec.isDynamic srs then 
                // copy dynamic subtensor out of the src
                execItemsForCopyFromDynamicSubtensor dfltChTrgt 
                    srcsDfltCh.[0] srs (List.tail srcsDfltCh)
            else [] // symbolic subtensor uses a slice of the src view
        | UNaryOp (SetSubtensor srs) ->
            // copy "a" if necessary
            let copyItems = 
                if dfltChTrgt <> srcsDfltCh.[0] then 
                    copyExecItems dfltChTrgt srcsDfltCh.[0] else []
            // copy "b" into a
            let setItems =
                execItemsForCopyToDynamicSubtensor dfltChTrgt srs 
                    (List.skip 2 srcsDfltCh) srcsDfltCh.[1]
            copyItems @ setItems
        | UNaryOp (Elements (_, elemFunc)) ->
            execItemsForElements compileEnv dfltChTrgt elemFunc srcsDfltCh
        | UNaryOp (ExtensionOp eop) -> 
            (toCudaUOp eop).ExecItems compileEnv args helpers

                
    /// returns the execution units for tracing the result
    let traceItemsForExpr compileEnv {MemAllocator=memAllocator
                                      Target=trgtChs
                                      Expr=uexpr} =
        /// Default channel of target.
        let defaultChTrgt = trgtChs.[dfltChId]

        [Trace (uexpr, defaultChTrgt)]


    /// generates CUDA execution units that will evaluate the given unified expression
    let exprToCudaExecUnits (compileEnv: CudaCompileEnvT) =
        ExecUnit.exprToExecUnits {
            ExecItemsForOp=execItemsForOp compileEnv
            TraceItemsForExpr=traceItemsForExpr compileEnv
            TrgtGivenSrcs=trgtGivenSrcs compileEnv
            SrcReqs=srcReqs compileEnv
        } 




