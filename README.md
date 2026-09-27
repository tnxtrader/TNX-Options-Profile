# TNX Options Profile

A professional-grade options profile indicator for Quantower, modelled on the DeepCharts layout.

![Platform](https://img.shields.io/badge/platform-Quantower-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-purple)
![License](https://img.shields.io/badge/license-MIT-green)

## What It Does

A docked options profile panel that displays GEX, DEX, volume, and open interest split from a centre line. Includes peak lines, rolling M1/M5/M15/M30 outlines, zero gamma, max pain, and expected move bands.

## Features

- **Data sources**: GEX (call − put), GEX split, C/P Sigma, C OI − P OI, DEX
- **Expiration filter**: 0DTE, ≤1d, ≤7d, ≤31d, ≤91d, all
- **Peak lines**: Largest call-side and put-side strikes projected across the chart
- **Rolling values**: M1/M5/M15/M30 step outlines showing positioning shifts
- **Themes**: 29 built-in palettes including DeepCharts, ATAS X, Bookmap, TradingView
- **Panel controls**: dock left or right, drag edge to resize, opacity, background follow-theme

## Installation

1. Build the project: dotnet build


2. Copy `bin/TNXOptionsProfile.dll` into: C:\AMP Quantower\Settings\Scripts\Indicators\

 
3. Restart Quantower and add the indicator to any chart.

## Build Requirements

- .NET 10.0 SDK
- Quantower v1.146.7 (or compatible)
- Reference to `TradingPlatform.BusinessLayer.dll`

## Documentation

See [SPEC.md](SPEC.md) for the full implementation specification.

## License

MIT