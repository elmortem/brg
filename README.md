# Batch Renderer Group

A lightweight and efficient C# library for Unity that implements Batch Renderer Group functionality, enabling high-performance rendering of large numbers of objects.

## Installation

Installation as a unity module via a git link in PackageManager:
```
https://github.com/elmortem/brg.git?path=Packages/BRG
```
Or direct editing of `Packages/manifest' is supported.json:
```
"com.elmortem.brg": "https://github.com/elmortem/brg.git?path=Packages/BRG",
```

## Features

- Efficient batching of multiple instances for improved rendering performance
- Support for custom mesh, material, and compute shader
- Easy-to-use API for initializing and managing batched renderers
- Automatic culling and draw call optimization
- Color and transform customization per instance

## Usage

```csharp
// Create BRG items
BrgItem[] items = new BrgItem[]
{
    new BrgItem { Position = Vector3.zero, Rotation = Vector3.zero, Scale = Vector3.one, Color = Color.white },
    // Add more items as needed
};

// Initialize the BRG container
brgContainer.Init(mesh, material, computeShader, items);
```

## Requirements

- Unity 2022.3 or later
- Compute shader support