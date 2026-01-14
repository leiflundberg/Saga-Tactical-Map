# S.A.G.A Tactical Map OSINT Viewer
*Sea & Air Global Awareness*

Saga is a distributed Command & Control (C2) demonstrator. The intention is to create a map that allows a commander to view open source intelligence into a unified picture. The application aggregates real-time tracking data from disparate sources (air and sea), normalizes it via a cloud middleware and renders it on a Windows desktop client showing a geospatial map.

# Architecture
The codebase is organized as a monorepo and follows a modern architecture designed to decouple the frontend from external API volatility. I.e. if there are changes on the API layer or we wish to add a different sources this only needs to be changed in one place.

There are three distinct layers: 
- Saga.Client (WPF/.NET): Tactical display using Mapsui v5 for hardware-accelerated rendering and smooth animations
- Saga.Server (Azure Functions): Serverless code that fetches raw data from external providers and transforms into internal data contract
- Saga.Shared (DTO): Common library ensuring compile-time contract between client and server

# Technology Stack
- IDE: Visual Studio 2026
- Framework: .NET 10 with C#
- Client: WPF with MVVM architecture
- Map Engine: Mapsui 5.0 (OpenStreetMap)
- Backend: Azure Functions (Isolated HTTP trigger)

# Local Installation
1. Clone the repository.

2. Open `Saga.slnx` in Visual Studio 2026.

3. Right-click the Solution and select "Set Startup Projects".

4. Choose Multiple Startup Projects and set both Saga.Client and Saga.Server to Start.

5. Press F5 to launch the secure gateway and the client simultaneously.