# Smoothed Particle Hydrodynamics (Unity)

Real-time GPU fluid simulation using SPH, implemented as Unity compute shaders.

## Features

- **Two solvers** (toggle via `SolverMode`): WCSPH (Tait EOS) and PCISPH (iterative pressure projection)
- **Collision-free neighbor search**: linear grid → bitonic sort → cell start/end → reorder into a sorted buffer
- **Fidelity layers** (all runtime-tunable): surface tension (cohesion/curvature/adhesion), XSPH viscosity, vorticity confinement
- **Air friction / wind**: relative-velocity air drag on the free surface (`dv/dt = -k(v - v_air)`, implicit/stable)
- **Stability**: CFL sound-speed auto-substepping, NaN guard, velocity clamp, particle-count cap

## File Breakdown

- `Assets/Scripts/SPH.cs` — main solver driver (buffers, dispatch loop, substepping, diagnostics)
- `Assets/Shaders/SPHCompute.compute` — all SPH compute kernels (density/pressure, forces, grid, PCISPH, integration, air drag)
- `Assets/Scripts/FluidScreenSpaceRenderer.cs` — screen-space fluid rendering
- `Assets/Scripts/FluidRayMarching.cs` — ray-marched rendering alternative

## Air Friction / Wind controls (Inspector)

| Field | Default | Purpose |
|---|---|---|
| `enableAirDrag` | off | master toggle |
| `airDragCoeff` | 1.0 | drag rate k (1/s) |
| `windVelocity` | (0,0,0) | ambient air velocity — non-zero = wind |
| `airDragSurfaceOnly` | true | only exposed surface particles feel the air |
| `airDragSurfaceThreshold` | 0.9 | density fraction below which a particle counts as surface |

## Credits

This project started from [AJTech2002/Smoothed-Particle-Hydrodynamics](https://github.com/AJTech2002/Smoothed-Particle-Hydrodynamics)
(MIT-licensed) and has since diverged substantially — adding PCISPH, the collision-free grid neighbor search,
surface tension / XSPH / vorticity, air drag & wind, and CFL substepping. The original `LICENSE` is retained.
