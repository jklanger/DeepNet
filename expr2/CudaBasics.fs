﻿module CudaBasics

open ManagedCuda

/// CUDA block dimension
type BlockDimT = int * int * int

/// CUDA grid dimension
type GridDimT = int * int * int

/// convert block/grid dimension to VectorTypes.dim3
let toDim3 d =
    let (x: int), y, z = d
    VectorTypes.dim3(x, y, z)

/// CUDA launch dimension
type LaunchDimT = {Block: BlockDimT; Grid: GridDimT;}

/// CUDA context
let cudaCntxt = 
    let cudaCntxt = 
        try
            new CudaContext(createNew=false)
        with
        e ->
            printfn "Cannot create CUDA context: %s" e.Message
            exit 10

    let di = cudaCntxt.GetDeviceInfo()
    printfn "CUDA device:                                         %s" di.DeviceName
    printfn "CUDA driver version:                                 %A" di.DriverVersion
    printfn "CUDA device global memory:                           %A" di.TotalGlobalMemory
    printfn "CUDA device free memory:                             %A" (cudaCntxt.GetFreeDeviceMemorySize())
    printfn "CUDA device compute capability:                      %A" di.ComputeCapability
    printfn "CUDA device maximum block size:                      %A" di.MaxBlockDim
    printfn "CUDA device maximum grid size:                       %A" di.MaxGridDim
    printfn "CUDA device async engine count:                      %d" di.AsyncEngineCount
    printfn "CUDA device can execute kernels concurrently:        %A" di.ConcurrentKernels
    printfn "CUDA device can overlap kernels and memory transfer: %A" di.GpuOverlap

    cudaCntxt

let cudaDeviceInfo =
    cudaCntxt.GetDeviceInfo()

let cudaMaxBlockDim =
    int cudaDeviceInfo.MaxBlockDim.x, int cudaDeviceInfo.MaxBlockDim.y, int cudaDeviceInfo.MaxBlockDim.z

let cudaMaxGridDim =
    int cudaDeviceInfo.MaxGridDim.x, int cudaDeviceInfo.MaxGridDim.y, int cudaDeviceInfo.MaxGridDim.z
    
      
    







