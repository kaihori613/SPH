using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Runtime.InteropServices;

[System.Serializable]
[StructLayout(LayoutKind.Sequential, Size = 44)]
public struct Particle
{
    public float pressure;
    public float density;
    public Vector3 currentForce;
    public Vector3 velocity;
    public Vector3 position;
}

// Diffuse marker particle (Ihmsen 2012: unified spray/foam/bubbles). Cheap, advected
// ballistically or with the fluid depending on how much water surrounds it. type:
// -1 inactive, 0 spray (in air), 1 foam (at surface), 2 bubble (submerged).
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct DiffuseParticle
{
    public Vector3 position;
    public Vector3 velocity;
    public float lifetime;
    public int type;
}

public class SPH : MonoBehaviour
{
    [Header("General")]
    public Transform collisionSphere;
    public bool showSpheres = true;
    public Vector3Int numToSpawn = new Vector3Int(32, 32, 32);
    [Tooltip("Hard cap on spawned particles. The bitonic sort is O(n log^2 n) and runs every substep; a huge count freezes/crashes the GPU (driver timeout). If numToSpawn exceeds this, the lattice is scaled down. Raise only if your GPU can handle more.")]
    public int maxParticles = 131072;
    public Vector3 boxSize = new Vector3(15, 15, 15);
    public Vector3 spawnCenter;
    public float particleRadius = 0.1f;
    public float spawnJitter = 0.2f;

    [Header("Particle Rendering")]
    public Mesh particleMesh;
    public float particleRenderSize = 8f;
    public Material material;

    public enum ColorMode { Uniform, Speed, Density }
    [Tooltip("Uniform = flat color; Speed = blue->red by velocity magnitude (Seb Lague style); Density = by fluid density.")]
    public ColorMode particleColorMode = ColorMode.Speed;
    [Tooltip("Speed (m/s) at the blue end of the ramp (Speed mode).")]
    public float speedColorMin = 0f;
    [Tooltip("Speed (m/s) at the red end of the ramp (Speed mode).")]
    public float speedColorMax = 6f;
    [Tooltip("Density at the blue end, as a fraction of restingDensity (Density mode). Auto-scales with rho0.")]
    public float densityColorMinFrac = 0.9f;
    [Tooltip("Density at the red end, as a fraction of restingDensity (Density mode). Auto-scales with rho0.")]
    public float densityColorMaxFrac = 1.1f;
    [Range(0f, 2f)]
    [Tooltip("Emission strength so the colors glow (0 = lit only). Applied only in Speed/Density modes.")]
    public float colorEmission = 0.3f;

    [Header("Compute")]
    public ComputeShader shader;
    public Particle[] particles;

    public enum SolverMode { WCSPH, PCISPH }
    [Header("Solver")]
    [Tooltip("WCSPH = Tait EOS (compressible, stiffness+CFL). PCISPH = iterative pressure projection (incompressible).")]
    public SolverMode solverMode = SolverMode.WCSPH;
    [Tooltip("PCISPH pressure-correction iterations per step. More = closer to incompressible.")]
    [Range(1, 10)]
    public int solverIterations = 3;

    [Header("Fluid Constants")]
    public float boundDamping = -0.3f;
    public float viscosity = 0.05f;
    public float particleMass = 1f;  // used if autoMassFromRadius == false
    public float restingDensity = 1f;
    public float timeStep = 0.007f;
    [Tooltip("Tait speed of sound c0. Stable dt scales as ~cflLambda*h/c0.")]
    public float stiffness = 60f;

    [Header("Stability (CFL)")]
    [Tooltip("Auto-pick substeps so dt_sub <= cflLambda * h / stiffness (sound-speed CFL). Keeps stiff WCSPH from blowing up.")]
    public bool autoSubsteps = true;
    [Range(0.1f, 0.6f)]
    public float cflLambda = 0.4f;
    [Tooltip("Upper bound on substeps so a bad config can't freeze the editor.")]
    public int maxSubsteps = 16;

    [Header("Surface Tension")]
    public bool enableSurfaceTension = true;
    public float cohesionAlpha = 1.0f;
    public float curvatureGamma = 1.0f;
    public float adhesionBeta = 0.0f;
    public float supportRadius;

    [Header("XSPH Viscosity")]
    public float xsphAlpha = 0.05f;
    public bool enableXSPH = true;

    [Header("Vorticity")]
    public bool enableVorticity = true;
    public float vorticityEps = 0.1f;

    [Header("Air Friction / Wind")]
    [Tooltip("Air-fluid drag: surface velocity relaxes toward windVelocity as exp(-airDragCoeff*dt). Off by default.")]
    public bool enableAirDrag = false;
    [Tooltip("Drag rate k (1/s). ~0.5 = gentle air resistance, ~5 = strong. Stable at any value (implicit).")]
    public float airDragCoeff = 1.0f;
    [Tooltip("Ambient air velocity. Non-zero = wind that pushes the exposed surface.")]
    public Vector3 windVelocity = Vector3.zero;
    [Tooltip("Apply drag mainly to exposed free-surface particles (density-based) instead of the whole body. Physically, only the surface touches air.")]
    public bool airDragSurfaceOnly = true;
    [Tooltip("Particles below this fraction of rest density count as surface (full drag); the bulk (rho~=rho0) is shielded.")]
    [Range(0.5f, 1.0f)]
    public float airDragSurfaceThreshold = 0.9f;

    [Header("Boundary Particles (Akinci 2012)")]
    [Tooltip("Sample the box walls with boundary particles that contribute to density and push back with pressure (fixes wall density deficiency). Off by default; toggle to A/B against the analytic clamp. Read at Awake, so change it before entering play mode.")]
    public bool enableBoundaryParticles = false;
    [Range(1, 4)]
    [Tooltip("Wall sampling depth. More layers = fuller support near walls (and more boundary particles).")]
    public int boundaryLayers = 2;
    [Range(0.5f, 1.5f)]
    [Tooltip("Boundary sample spacing as a fraction of fluid spacing (2*particleRadius). <1 = denser walls.")]
    public float boundarySpacingScale = 1.0f;

    [Header("Diffuse Particles (Foam / Spray / Bubbles)")]
    [Tooltip("Ihmsen-2012 diffuse markers spawned where the fluid is aerated. Off by default.")]
    public bool enableDiffuse = false;
    [Tooltip("Size of the diffuse pool (ring buffer). Memory + draw cost scale with this.")]
    public int maxDiffuse = 40000;
    [Tooltip("White foam material. Auto-created from the Instanced/DiffuseParticle shader if left empty.")]
    public Material diffuseMaterial;
    [Tooltip("Render size of foam specks (x particleRadius).")]
    public float diffuseRenderSize = 3f;
    [Header("Diffuse — generation")]
    [Tooltip("Overall spawn-rate multiplier. 0 = none.")]
    public float diffuseGenRate = 0.6f;
    [Tooltip("Speed (m/s) mapped to 0..1 kinetic potential: below min spawns nothing, above max is full.")]
    public float diffuseKEmin = 2f;
    public float diffuseKEmax = 12f;
    [Tooltip("Weight of the trapped-air term (fluid crashing/shearing into itself).")]
    public float diffuseTrappedAir = 1.0f;
    [Tooltip("Weight of the wave-crest term (surface moving outward along its normal).")]
    public float diffuseWaveCrest = 1.0f;
    [Range(0, 16)]
    [Tooltip("Max diffuse particles a single fluid particle can spawn per frame.")]
    public int diffuseMaxSpawnPerParticle = 4;
    [Header("Diffuse — dynamics")]
    [Tooltip("Lifetime range (seconds) assigned on spawn.")]
    public float diffuseLifetimeMin = 2f;
    public float diffuseLifetimeMax = 6f;
    [Range(0f, 1f)]
    [Tooltip("Local density (fraction of rho0) below which a marker is spray (ballistic in air).")]
    public float diffuseSprayThreshold = 0.2f;
    [Range(0f, 1f)]
    [Tooltip("Local density (fraction of rho0) above which a marker is a submerged bubble (rises).")]
    public float diffuseBubbleThreshold = 0.8f;
    [Tooltip("Bubble buoyancy as a fraction of gravity (upward).")]
    public float diffuseBuoyancy = 0.8f;
    [Tooltip("How strongly foam/bubbles are pulled toward the local fluid velocity.")]
    public float diffuseDrag = 8f;

    [Header("Auto spacing")]
    public bool autoMassFromRadius = true;  // mass = rho0 * spacing^3
    public bool clampSupportRadius = true;  // keep h ~ 2x spacing

    [Header("Time Integration")]
    [Min(1)]
    public int substeps = 1; // used only when autoSubsteps == false

    [Header("Diagnostics")]
    [Tooltip("Log avg density / velocity / bbox every logInterval FixedUpdates. rho~restingDensity + V~0 = settled pool.")]
    public bool logDiagnostics = false;
    [Min(1)]
    public int logInterval = 60;
    private int _logCounter;
    private Particle[] _readback;

    // Private variables
    private ComputeBuffer _argsBuffer;
    public ComputeBuffer _particlesBuffer;        // master unsorted buffer (stable IDs)
    public ComputeBuffer _sortedParticlesBuffer;  // sorted linear buffer for fast physics
    private ComputeBuffer _particleIndices;
    private ComputeBuffer _particleNormalsBuffer;
    private ComputeBuffer _xsphDelta;
    private ComputeBuffer _vorticity;
    private ComputeBuffer _sortKeys;
    private ComputeBuffer _cellStarts; // uint per cell (start index into sorted list) or 0xFFFFFFFF if empty
    private ComputeBuffer _cellEnds;   // uint per cell (end index, exclusive)
    private ComputeBuffer _predPos;    // PCISPH predicted position (sorted-slot)
    private ComputeBuffer _predVel;    // PCISPH predicted velocity (sorted-slot)
    private ComputeBuffer _predAccel;  // PCISPH pressure acceleration scratch (sorted-slot)

    // Akinci boundary particles (static walls)
    private ComputeBuffer _boundaryPositions;
    private ComputeBuffer _boundaryPsi;
    private ComputeBuffer _boundaryCellStarts;
    private ComputeBuffer _boundaryCellEnds;
    private int _boundaryCount;
    private bool _boundaryReady;

    // Diffuse particles (foam/spray/bubbles)
    private ComputeBuffer _diffuseBuffer;   // fixed pool, ring-buffer allocation
    private ComputeBuffer _diffuseCount;    // 1 uint: monotonic ring head
    private ComputeBuffer _diffuseArgsBuffer;
    private int _diffuseGenerateKernel;
    private int _diffuseAdvectKernel;
    private int _actualMaxDiffuse;
    private bool _diffuseReady;
    private int _diffuseFrame;

    // Kernel IDs
    private int integrateKernel;
    private int computeForceKernel;
    private int densityPressureKernel;
    private int computeNormalsKernel;
    private int sortKernel;
    private int calculateCellStartEndKernel;
    private int computeXsphKernel;
    private int applyXsphKernel;
    private int computeVorticityKernel;
    private int applyVorticityKernel;
    private int initSortKeysKernel;
    private int clearCellRangesKernel;
    private int reorderParticlesKernel;

    // PCISPH kernels
    private int pciDensityKernel;
    private int pciNonPressureKernel;
    private int pciPredictKernel;
    private int pciPressureUpdateKernel;
    private int pciPressureAccelKernel;
    private int pciApplyAccelKernel;
    private int pciPressureForceKernel;

    // Cached PCISPH delta (recomputed only when h/mass/dt change)
    private float _cachedDelta;
    private float _deltaH = -1f, _deltaMass = -1f, _deltaDt = -1f;

    // State
    private int totalParticles;
    private int paddedParticles;
    private int3 _gridRes;    // matched on the compute side
    private int _gridCellCount;
    private const int THREADS = 256;

    private struct int3
    {
        public int x, y, z;
        public int3(int X, int Y, int Z) { x = X; y = Y; z = Z; }
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.blue;
        Gizmos.DrawWireCube(Vector3.zero, boxSize);

        if (!Application.isPlaying)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(spawnCenter, 0.1f);
        }
    }

    private void Awake()
    {
        // Safety: keep the spawn count under maxParticles. The neighbour search sorts the
        // whole (padded) buffer with a bitonic sort every substep — O(n log^2 n) dispatches —
        // so an over-large numToSpawn (e.g. typed into the Inspector) can hang the GPU past the
        // driver's watchdog timeout and take the editor down. Scale the lattice down instead.
        int requested = numToSpawn.x * numToSpawn.y * numToSpawn.z;
        int cap = Mathf.Max(1, maxParticles);
        if (requested > cap)
        {
            float scale = Mathf.Pow((float)cap / requested, 1f / 3f);
            Vector3Int clamped = new Vector3Int(
                Mathf.Max(1, Mathf.FloorToInt(numToSpawn.x * scale)),
                Mathf.Max(1, Mathf.FloorToInt(numToSpawn.y * scale)),
                Mathf.Max(1, Mathf.FloorToInt(numToSpawn.z * scale)));
            Debug.LogError($"[SPH] numToSpawn {numToSpawn} = {requested} particles exceeds maxParticles ({cap}). " +
                           $"Clamped to {clamped} = {clamped.x * clamped.y * clamped.z} to avoid a GPU timeout/crash. " +
                           "Lower numToSpawn, or raise maxParticles if your GPU can handle more.");
            numToSpawn = clamped;
        }

        totalParticles = numToSpawn.x * numToSpawn.y * numToSpawn.z;
        paddedParticles = Mathf.NextPowerOfTwo(totalParticles);

        SpawnParticleInBox();
        SetupComputeBuffers();
        SetupBoundary();
        SetupDiffuse();
    }

    // ---- Akinci boundary particles (static walls) -------------------------------------------
    private int BoundaryCellLinear(int cx, int cy, int cz)
        => cx + cy * _gridRes.x + cz * _gridRes.x * _gridRes.y;

    private int BoundaryCellOf(Vector3 p, float invH)
    {
        Vector3 half = boxSize * 0.5f;
        Vector3 g = (p + half) * invH;
        int cx = Mathf.Clamp(Mathf.FloorToInt(g.x), 0, _gridRes.x - 1);
        int cy = Mathf.Clamp(Mathf.FloorToInt(g.y), 0, _gridRes.y - 1);
        int cz = Mathf.Clamp(Mathf.FloorToInt(g.z), 0, _gridRes.z - 1);
        return BoundaryCellLinear(cx, cy, cz);
    }

    // Samples the 6 box faces, computes each particle's Akinci Psi (rho0 * volume) from its
    // boundary neighbours, sorts them into the fluid grid, and uploads. Built once at startup
    // because the walls are static. Independent of the fluid buffers.
    // Bind 1-element placeholders so the density/force kernels always have the boundary buffers
    // bound (they're only read when enableBoundary==1, which stays 0 until _boundaryReady).
    private void BindBoundaryDummies()
    {
        _boundaryPositions = new ComputeBuffer(1, sizeof(float) * 3);
        _boundaryPsi = new ComputeBuffer(1, sizeof(float));
        _boundaryCellStarts = new ComputeBuffer(1, sizeof(uint));
        _boundaryCellEnds = new ComputeBuffer(1, sizeof(uint));
        _boundaryCellStarts.SetData(new uint[] { 0xffffffffu });
        _boundaryCellEnds.SetData(new uint[] { 0xffffffffu });
        foreach (int k in new[] { densityPressureKernel, computeForceKernel,
                                  pciDensityKernel, pciPressureUpdateKernel,
                                  pciPressureAccelKernel, pciPressureForceKernel })
        {
            shader.SetBuffer(k, "_boundaryPositions", _boundaryPositions);
            shader.SetBuffer(k, "_boundaryPsi", _boundaryPsi);
            shader.SetBuffer(k, "_boundaryCellStarts", _boundaryCellStarts);
            shader.SetBuffer(k, "_boundaryCellEnds", _boundaryCellEnds);
        }
        _boundaryReady = false;
    }

    private void SetupBoundary()
    {
        // Sampling the walls and solving Psi costs real CPU time and four GPU buffers, so skip it
        // entirely when the feature is off. Toggling it on mid-play therefore does nothing; the
        // walls are only built at Awake.
        if (!enableBoundaryParticles || _gridCellCount <= 0) { BindBoundaryDummies(); return; }

        // Wired into both solver paths: the WCSPH density/force kernels and the PCISPH
        // density/pressure-update/accel/force kernels all read these buffers.
        float spacing = 2f * particleRadius;
        float hTarget = 2.0f * spacing;
        float hValue = supportRadius > 0f ? supportRadius : hTarget;
        if (clampSupportRadius) hValue = Mathf.Clamp(hValue, 1.8f * spacing, 2.4f * spacing);
        float invH = 1f / hValue;
        float h2 = hValue * hValue;
        float poly6C = 315f / (64f * Mathf.PI * Mathf.Pow(hValue, 9f));
        float bs = Mathf.Max(0.1f, spacing * boundarySpacingScale);

        // 1. Sample the 6 faces, boundaryLayers deep (each layer offset outward into the solid).
        List<Vector3> pts = new List<Vector3>();
        Vector3 half = boxSize * 0.5f;
        int cx = Mathf.Max(2, Mathf.RoundToInt(boxSize.x / bs) + 1);
        int cy = Mathf.Max(2, Mathf.RoundToInt(boxSize.y / bs) + 1);
        int cz = Mathf.Max(2, Mathf.RoundToInt(boxSize.z / bs) + 1);

        for (int l = 0; l < Mathf.Max(1, boundaryLayers); l++)
        {
            float off = l * bs;
            for (int a = 0; a < cy; a++)
                for (int b = 0; b < cz; b++)
                {
                    float y = Mathf.Lerp(-half.y, half.y, a / (float)(cy - 1));
                    float z = Mathf.Lerp(-half.z, half.z, b / (float)(cz - 1));
                    pts.Add(new Vector3(-half.x - off, y, z));
                    pts.Add(new Vector3( half.x + off, y, z));
                }
            for (int a = 0; a < cx; a++)
                for (int b = 0; b < cz; b++)
                {
                    float x = Mathf.Lerp(-half.x, half.x, a / (float)(cx - 1));
                    float z = Mathf.Lerp(-half.z, half.z, b / (float)(cz - 1));
                    pts.Add(new Vector3(x, -half.y - off, z));
                    pts.Add(new Vector3(x,  half.y + off, z));
                }
            for (int a = 0; a < cx; a++)
                for (int b = 0; b < cy; b++)
                {
                    float x = Mathf.Lerp(-half.x, half.x, a / (float)(cx - 1));
                    float y = Mathf.Lerp(-half.y, half.y, b / (float)(cy - 1));
                    pts.Add(new Vector3(x, y, -half.z - off));
                    pts.Add(new Vector3(x, y,  half.z + off));
                }
        }

        _boundaryCount = pts.Count;
        if (_boundaryCount == 0) { BindBoundaryDummies(); return; }

        Vector3[] pos = pts.ToArray();
        int[] cellOf = new int[_boundaryCount];
        Dictionary<int, List<int>> cellMap = new Dictionary<int, List<int>>();
        for (int i = 0; i < _boundaryCount; i++)
        {
            int c = BoundaryCellOf(pos[i], invH);
            cellOf[i] = c;
            if (!cellMap.TryGetValue(c, out List<int> lst)) { lst = new List<int>(); cellMap[c] = lst; }
            lst.Add(i);
        }

        // 2. Psi_b = rho0 / sum_b' W(x_b - x_b')  (sum over boundary neighbours incl. self).
        float[] psi = new float[_boundaryCount];
        for (int i = 0; i < _boundaryCount; i++)
        {
            int bcx = cellOf[i] % _gridRes.x;
            int bcy = (cellOf[i] / _gridRes.x) % _gridRes.y;
            int bcz = cellOf[i] / (_gridRes.x * _gridRes.y);
            float sum = 0f;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int nx = bcx + dx, ny = bcy + dy, nz = bcz + dz;
                        if (nx < 0 || ny < 0 || nz < 0 || nx >= _gridRes.x || ny >= _gridRes.y || nz >= _gridRes.z) continue;
                        if (!cellMap.TryGetValue(BoundaryCellLinear(nx, ny, nz), out List<int> lst)) continue;
                        foreach (int j in lst)
                        {
                            float r2 = (pos[i] - pos[j]).sqrMagnitude;
                            if (r2 < h2) { float t = h2 - r2; sum += poly6C * t * t * t; }
                        }
                    }
            psi[i] = sum > 1e-12f ? restingDensity / sum : 0f;
        }

        // 3. Sort by cell and build cell start/end over the fluid grid's cell count.
        int[] order = new int[_boundaryCount];
        for (int i = 0; i < _boundaryCount; i++) order[i] = i;
        System.Array.Sort(order, (x, y) => cellOf[x].CompareTo(cellOf[y]));

        Vector3[] sortedPos = new Vector3[_boundaryCount];
        float[] sortedPsi = new float[_boundaryCount];
        uint[] starts = new uint[_gridCellCount];
        uint[] ends = new uint[_gridCellCount];
        for (int c = 0; c < _gridCellCount; c++) { starts[c] = 0xffffffffu; ends[c] = 0xffffffffu; }

        for (int i = 0; i < _boundaryCount; i++)
        {
            int src = order[i];
            sortedPos[i] = pos[src];
            sortedPsi[i] = psi[src];
            int cellNow = cellOf[src];
            int cellPrev = i == 0 ? -1 : cellOf[order[i - 1]];
            int cellNext = i == _boundaryCount - 1 ? -1 : cellOf[order[i + 1]];
            if (cellNow != cellPrev) starts[cellNow] = (uint)i;
            if (cellNow != cellNext) ends[cellNow] = (uint)(i + 1);
        }

        // 4. Upload + bind to the WCSPH and PCISPH density/pressure kernels.
        _boundaryPositions = new ComputeBuffer(_boundaryCount, sizeof(float) * 3);
        _boundaryPsi = new ComputeBuffer(_boundaryCount, sizeof(float));
        _boundaryCellStarts = new ComputeBuffer(_gridCellCount, sizeof(uint));
        _boundaryCellEnds = new ComputeBuffer(_gridCellCount, sizeof(uint));
        _boundaryPositions.SetData(sortedPos);
        _boundaryPsi.SetData(sortedPsi);
        _boundaryCellStarts.SetData(starts);
        _boundaryCellEnds.SetData(ends);

        foreach (int k in new[] { densityPressureKernel, computeForceKernel,
                                  pciDensityKernel, pciPressureUpdateKernel,
                                  pciPressureAccelKernel, pciPressureForceKernel })
        {
            shader.SetBuffer(k, "_boundaryPositions", _boundaryPositions);
            shader.SetBuffer(k, "_boundaryPsi", _boundaryPsi);
            shader.SetBuffer(k, "_boundaryCellStarts", _boundaryCellStarts);
            shader.SetBuffer(k, "_boundaryCellEnds", _boundaryCellEnds);
        }

        _boundaryReady = true;
        Debug.Log($"[SPH] Boundary particles: {_boundaryCount} samples ({boundaryLayers} layer(s)) over the box walls.");
    }

    // Diffuse particle pool + kernels + render material. Independent of the fluid buffers so it
    // can be toggled on/off without touching the verified solver path.
    private void SetupDiffuse()
    {
        // Non-fatal: if the diffuse kernels aren't in the compiled shader (e.g. a compile error),
        // skip the whole subsystem rather than throwing in Awake and taking down the fluid sim.
        if (!shader.HasKernel("DiffuseGenerate") || !shader.HasKernel("DiffuseAdvect"))
        {
            Debug.LogWarning("[SPH] Diffuse kernels not found in SPHCompute; foam/spray disabled this session.");
            return;
        }

        _actualMaxDiffuse = Mathf.Max(256, maxDiffuse);

        _diffuseGenerateKernel = shader.FindKernel("DiffuseGenerate");
        _diffuseAdvectKernel = shader.FindKernel("DiffuseAdvect");

        _diffuseBuffer = new ComputeBuffer(_actualMaxDiffuse, 32);
        DiffuseParticle[] init = new DiffuseParticle[_actualMaxDiffuse];
        for (int i = 0; i < _actualMaxDiffuse; i++) init[i].type = -1; // all inactive
        _diffuseBuffer.SetData(init);

        _diffuseCount = new ComputeBuffer(1, sizeof(uint));
        _diffuseCount.SetData(new uint[] { 0 });

        uint[] dargs = {
            particleMesh.GetIndexCount(0),
            (uint)_actualMaxDiffuse,
            particleMesh.GetIndexStart(0),
            particleMesh.GetBaseVertex(0),
            0
        };
        _diffuseArgsBuffer = new ComputeBuffer(1, dargs.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        _diffuseArgsBuffer.SetData(dargs);

        // Both diffuse kernels read the fluid grid (sorted particles + cell ranges); generation
        // also reads the surface normals. Bind the same fluid buffers used by the SPH passes.
        int[] diffuseKernels = { _diffuseGenerateKernel, _diffuseAdvectKernel };
        foreach (int k in diffuseKernels)
        {
            shader.SetBuffer(k, "_sortedParticles", _sortedParticlesBuffer);
            shader.SetBuffer(k, "_cellStarts", _cellStarts);
            shader.SetBuffer(k, "_cellEnds", _cellEnds);
            shader.SetBuffer(k, "_particleNormals", _particleNormalsBuffer);
            shader.SetBuffer(k, "_diffuseParticles", _diffuseBuffer);
            shader.SetBuffer(k, "_diffuseCount", _diffuseCount);
        }

        if (diffuseMaterial == null)
        {
            Shader s = Shader.Find("Instanced/DiffuseParticle");
            if (s != null) diffuseMaterial = new Material(s);
            else Debug.LogWarning("[SPH] Instanced/DiffuseParticle shader not found; diffuse particles won't render. " +
                                  "Assign a diffuseMaterial or ensure DiffuseParticle.shader is imported.");
        }

        _diffuseReady = true;
    }

    // Clamp one axis of the spawn min-corner so the whole lattice stays inside the box
    // (with a margin). If the lattice is larger than the usable span, center it instead.
    private static float FitSpawnAxis(float corner, float extent, float half, float margin)
    {
        float lo = -half + margin;
        float hi =  half - margin - extent;
        if (hi < lo) return -0.5f * extent;      // lattice bigger than box: center on this axis
        return Mathf.Clamp(corner, lo, hi);
    }

    private void SpawnParticleInBox()
    {
        List<Particle> particleList = new List<Particle>();

        // Keep the spawn lattice fully inside the box. Spawning even partly outside makes the
        // frame-1 wall clamp pile particles onto a wall plane -> overlap -> pressure explosion.
        float spacing = 2f * particleRadius;
        Vector3 extent = new Vector3((numToSpawn.x - 1) * spacing,
                                     (numToSpawn.y - 1) * spacing,
                                     (numToSpawn.z - 1) * spacing);
        Vector3 half = boxSize * 0.5f;
        float margin = 1.5f * particleRadius;
        Vector3 spawnPoint = new Vector3(
            FitSpawnAxis(spawnCenter.x, extent.x, half.x, margin),
            FitSpawnAxis(spawnCenter.y, extent.y, half.y, margin),
            FitSpawnAxis(spawnCenter.z, extent.z, half.z, margin));
        if ((spawnPoint - spawnCenter).sqrMagnitude > 1e-6f)
            Debug.LogWarning($"[SPH] spawnCenter {spawnCenter} would place particles outside the box; " +
                             $"clamped to {spawnPoint} (lattice extent {extent}, box {boxSize}).");

        for (int x = 0; x < numToSpawn.x; x++)
        {
            for (int y = 0; y < numToSpawn.y; y++)
            {
                for (int z = 0; z < numToSpawn.z; z++)
                {
                    Vector3 spawnPosition = spawnPoint + new Vector3(x * particleRadius * 2, y * particleRadius * 2, z * particleRadius * 2);
                    spawnPosition += Random.onUnitSphere * particleRadius * spawnJitter;

                    Particle p = new Particle
                    {
                        position = spawnPosition,
                        velocity = Vector3.zero,
                        currentForce = Vector3.zero,
                        density = restingDensity,   // initialize to rest density to avoid div-by-zero
                        pressure = 0f,
                    };

                    particleList.Add(p);
                }
            }
        }

        // Real particles first, then dummies parked at infinity (never find neighbors).
        Particle[] particleArray = new Particle[paddedParticles];
        for (int i = 0; i < paddedParticles; i++)
        {
            if (i < totalParticles)
            {
                particleArray[i] = particleList[i];
            }
            else
            {
                particleArray[i] = new Particle
                {
                    position = new Vector3(999999, 999999, 999999),
                    density = 1, // prevent div/0
                    pressure = 0
                };
            }
        }

        _particlesBuffer = new ComputeBuffer(paddedParticles, 44);
        _particlesBuffer.SetData(particleArray);
    }

    private void SetupComputeBuffers()
    {
        // 1. Indirect args for instanced rendering
        uint[] args = {
            particleMesh.GetIndexCount(0),
            (uint)totalParticles,
            particleMesh.GetIndexStart(0),
            particleMesh.GetBaseVertex(0),
            0
        };
        _argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        _argsBuffer.SetData(args);

        // 2. Buffers
        _sortedParticlesBuffer = new ComputeBuffer(paddedParticles, 44);
        _particleIndices = new ComputeBuffer(paddedParticles, sizeof(uint));
        _sortKeys = new ComputeBuffer(paddedParticles, sizeof(uint));
        _particleNormalsBuffer = new ComputeBuffer(paddedParticles, sizeof(float) * 3);
        _xsphDelta = new ComputeBuffer(paddedParticles, sizeof(float) * 3);
        _vorticity = new ComputeBuffer(paddedParticles, sizeof(float) * 3);
        _predPos = new ComputeBuffer(paddedParticles, sizeof(float) * 3);
        _predVel = new ComputeBuffer(paddedParticles, sizeof(float) * 3);
        _predAccel = new ComputeBuffer(paddedParticles, sizeof(float) * 3);

        uint[] indices = new uint[paddedParticles];
        for (int i = 0; i < paddedParticles; i++) indices[i] = (uint)i;
        _particleIndices.SetData(indices);

        // 3. Kernels
        integrateKernel = shader.FindKernel("Integrate");
        computeForceKernel = shader.FindKernel("ComputeForces");
        densityPressureKernel = shader.FindKernel("ComputeDensityPressure");
        computeNormalsKernel = shader.FindKernel("ComputeNormals");
        sortKernel = shader.FindKernel("BitonicSort");
        calculateCellStartEndKernel = shader.FindKernel("CalculateCellStartEnd");
        computeXsphKernel = shader.FindKernel("ComputeXSPH");
        applyXsphKernel = shader.FindKernel("ApplyXSPH");
        computeVorticityKernel = shader.FindKernel("ComputeVorticity");
        applyVorticityKernel = shader.FindKernel("ApplyVorticityConfinement");
        clearCellRangesKernel = shader.FindKernel("ClearCellRanges");
        initSortKeysKernel = shader.FindKernel("InitSortKeys");
        reorderParticlesKernel = shader.FindKernel("ReorderParticles");

        pciDensityKernel = shader.FindKernel("PCIComputeDensity");
        pciNonPressureKernel = shader.FindKernel("PCINonPressureForces");
        pciPredictKernel = shader.FindKernel("PCIPredictStep");
        pciPressureUpdateKernel = shader.FindKernel("PCIPressureUpdate");
        pciPressureAccelKernel = shader.FindKernel("PCIPressureAccel");
        pciApplyAccelKernel = shader.FindKernel("PCIApplyAccel");
        pciPressureForceKernel = shader.FindKernel("PCIComputePressureForce");

        // 4. Support radius + grid resolution
        float s = 2f * particleRadius;
        float hTarget = 2.0f * s;
        float hValue = supportRadius > 0f ? supportRadius : hTarget;
        if (clampSupportRadius) hValue = Mathf.Clamp(hValue, 1.8f * s, 2.4f * s);

        int rx = Mathf.Max(1, Mathf.CeilToInt(boxSize.x / hValue));
        int ry = Mathf.Max(1, Mathf.CeilToInt(boxSize.y / hValue));
        int rz = Mathf.Max(1, Mathf.CeilToInt(boxSize.z / hValue));
        _gridRes = new int3(rx, ry, rz);
        _gridCellCount = rx * ry * rz;

        _cellStarts?.Release();
        _cellEnds?.Release();
        _cellStarts = new ComputeBuffer(_gridCellCount, sizeof(uint));
        _cellEnds = new ComputeBuffer(_gridCellCount, sizeof(uint));

        // 5. Constants (uploaded once at startup)
        shader.SetInt("particleCount", totalParticles);
        shader.SetInt("sortLength", paddedParticles);
        shader.SetInts("_gridRes", new int[] { rx, ry, rz });
        shader.SetInt("_gridCellCount", _gridCellCount);
        shader.SetFloat("viscosity", viscosity);
        shader.SetFloat("restDensity", restingDensity);
        shader.SetFloat("boundDamping", boundDamping);
        shader.SetFloat("stiffness", stiffness);
        shader.SetVector("boxSize", boxSize);
        shader.SetFloat("particleRadius", particleRadius);

        shader.SetInt("enableSurfaceTension", enableSurfaceTension ? 1 : 0);
        shader.SetFloat("gamma_curvature", curvatureGamma);
        shader.SetFloat("alpha_cohesion", cohesionAlpha);
        shader.SetFloat("beta_adhesion", adhesionBeta);

        shader.SetInt("enableXSPH", enableXSPH ? 1 : 0);
        shader.SetFloat("xsphAlpha", xsphAlpha);

        shader.SetInt("enableVorticity", enableVorticity ? 1 : 0);
        shader.SetFloat("vorticityEps", vorticityEps);

        // 6. Bind buffers
        shader.SetBuffer(reorderParticlesKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(reorderParticlesKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(reorderParticlesKernel, "_sortedParticles", _sortedParticlesBuffer);

        SetBufferOnKernels(densityPressureKernel);
        SetBufferOnKernels(computeForceKernel);
        SetBufferOnKernels(computeNormalsKernel);
        SetBufferOnKernels(computeXsphKernel);
        SetBufferOnKernels(applyXsphKernel);
        SetBufferOnKernels(computeVorticityKernel);
        SetBufferOnKernels(applyVorticityKernel);

        // PCISPH kernels: sorted buffer + cell ranges + predicted-state buffers
        int[] pciKernels = {
            pciDensityKernel, pciNonPressureKernel, pciPredictKernel,
            pciPressureUpdateKernel, pciPressureAccelKernel, pciApplyAccelKernel,
            pciPressureForceKernel
        };
        foreach (int kernel in pciKernels)
        {
            SetBufferOnKernels(kernel);
            shader.SetBuffer(kernel, "_predPos", _predPos);
            shader.SetBuffer(kernel, "_predVel", _predVel);
            shader.SetBuffer(kernel, "_predAccel", _predAccel);
        }

        // Integrate reads sorted, writes to master via the index map
        shader.SetBuffer(integrateKernel, "_sortedParticles", _sortedParticlesBuffer);
        shader.SetBuffer(integrateKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(integrateKernel, "_particleIndices", _particleIndices);

        // Sort + grid
        shader.SetBuffer(initSortKeysKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(initSortKeysKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(initSortKeysKernel, "_sortKeys", _sortKeys);

        shader.SetBuffer(sortKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(sortKernel, "_sortKeys", _sortKeys);

        shader.SetBuffer(calculateCellStartEndKernel, "_sortKeys", _sortKeys);
        shader.SetBuffer(calculateCellStartEndKernel, "_cellStarts", _cellStarts);
        shader.SetBuffer(calculateCellStartEndKernel, "_cellEnds", _cellEnds);

        shader.SetBuffer(clearCellRangesKernel, "_cellStarts", _cellStarts);
        shader.SetBuffer(clearCellRangesKernel, "_cellEnds", _cellEnds);
    }

    private void SetBufferOnKernels(int kernel)
    {
        shader.SetBuffer(kernel, "_sortedParticles", _sortedParticlesBuffer);
        shader.SetBuffer(kernel, "_cellStarts", _cellStarts);
        shader.SetBuffer(kernel, "_cellEnds", _cellEnds);
        shader.SetBuffer(kernel, "_particleNormals", _particleNormalsBuffer);
        shader.SetBuffer(kernel, "_xsphDelta", _xsphDelta);
        shader.SetBuffer(kernel, "_vorticity", _vorticity);
    }

    // Per-frame upload of the scalar physics knobs so they're tunable live in the Inspector
    // during Play (viscosity, surface tension, XSPH, vorticity, stiffness). Structural values
    // (particle count, box, grid) still require a restart since buffers are sized once.
    private void UploadTunables()
    {
        shader.SetFloat("viscosity", viscosity);
        shader.SetFloat("restDensity", restingDensity);
        shader.SetFloat("boundDamping", boundDamping);
        shader.SetFloat("stiffness", stiffness);

        shader.SetInt("enableSurfaceTension", enableSurfaceTension ? 1 : 0);
        shader.SetFloat("gamma_curvature", curvatureGamma);
        shader.SetFloat("alpha_cohesion", cohesionAlpha);
        shader.SetFloat("beta_adhesion", adhesionBeta);

        shader.SetInt("enableXSPH", enableXSPH ? 1 : 0);
        shader.SetFloat("xsphAlpha", xsphAlpha);

        shader.SetInt("enableVorticity", enableVorticity ? 1 : 0);
        shader.SetFloat("vorticityEps", vorticityEps);

        shader.SetInt("enableAirDrag", enableAirDrag ? 1 : 0);
        shader.SetFloat("airDragCoeff", airDragCoeff);
        shader.SetVector("windVelocity", windVelocity);
        shader.SetInt("airDragSurfaceOnly", airDragSurfaceOnly ? 1 : 0);
        shader.SetFloat("airDragSurfaceThreshold", airDragSurfaceThreshold);

        shader.SetInt("enableBoundary", (enableBoundaryParticles && _boundaryReady) ? 1 : 0);
    }

    // Live-tunable diffuse-particle knobs, uploaded when the system is active.
    private void UploadDiffuseTunables(float dt)
    {
        shader.SetInt("maxDiffuse", _actualMaxDiffuse);
        shader.SetFloat("_diffuseDt", dt);
        shader.SetFloat("diffuseGenRate", diffuseGenRate);
        shader.SetFloat("diffuseKEmin", diffuseKEmin);
        shader.SetFloat("diffuseKEmax", diffuseKEmax);
        shader.SetFloat("diffuseTrappedAir", diffuseTrappedAir);
        shader.SetFloat("diffuseWaveCrest", diffuseWaveCrest);
        shader.SetInt("diffuseMaxSpawnPerParticle", diffuseMaxSpawnPerParticle);
        shader.SetFloat("diffuseLifetimeMin", diffuseLifetimeMin);
        shader.SetFloat("diffuseLifetimeMax", diffuseLifetimeMax);
        shader.SetFloat("diffuseSprayThreshold", diffuseSprayThreshold);
        shader.SetFloat("diffuseBubbleThreshold", diffuseBubbleThreshold);
        shader.SetFloat("diffuseBuoyancy", diffuseBuoyancy);
        shader.SetFloat("diffuseDrag", diffuseDrag);
    }

    // Uploads all h/mass dependent kernel coefficients.
    private void UploadKernelConstants(float h, float mass)
    {
        float h2 = h * h;
        float h3 = h2 * h;
        float h6 = h3 * h3;
        float h9 = h6 * h3;

        float invH = 1f / h;
        float poly6C = 315f / (64f * Mathf.PI * h9);
        float kSpikyGrad = -45f / (Mathf.PI * h6);
        float kViscLap = 45f / (Mathf.PI * h6);
        float cohesionCommon = 32f / (Mathf.PI * h9);
        float h6over64 = h6 / 64f;
        float adhesionC = 0.007f / Mathf.Pow(h, 3.25f);

        shader.SetFloat("particleMass", mass);
        shader.SetFloat("h", h);
        shader.SetFloat("h2", h2);
        shader.SetFloat("h3", h3);
        shader.SetFloat("h9", h9);
        shader.SetFloat("invH", invH);
        shader.SetFloat("poly6C", poly6C);
        shader.SetFloat("kSpikyGrad", kSpikyGrad);
        shader.SetFloat("kViscLap", kViscLap);
        shader.SetFloat("cohesionCommon", cohesionCommon);
        shader.SetFloat("h6over64", h6over64);
        shader.SetFloat("adhesionC", adhesionC);
    }

    // Standard PCISPH delta from a full rest-configuration prototype neighbourhood
    // (regular lattice at particle spacing). delta = -1 / (beta * (-|sum gradW|^2 - sum(gradW.gradW))).
    private float ComputePCISPHDelta(float h, float mass, float dt)
    {
        float spacing = 2f * particleRadius;
        float h6 = Mathf.Pow(h, 6f);
        Vector3 sumGrad = Vector3.zero;
        float sumGradDot = 0f;

        int R = Mathf.CeilToInt(h / spacing);
        for (int x = -R; x <= R; x++)
            for (int y = -R; y <= R; y++)
                for (int z = -R; z <= R; z++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    Vector3 r = new Vector3(x, y, z) * spacing;
                    float dist = r.magnitude;
                    if (dist < h)
                    {
                        float term = h - dist;
                        Vector3 gradW = (-45f / (Mathf.PI * h6) * term * term) * (r / dist);
                        sumGrad += gradW;
                        sumGradDot += Vector3.Dot(gradW, gradW);
                    }
                }

        float beta = 2f * (dt * mass) * (dt * mass) / (restingDensity * restingDensity);
        float denom = -Vector3.Dot(sumGrad, sumGrad) - sumGradDot;
        if (Mathf.Abs(denom) < 1e-9f) return 0f;
        return -1f / (beta * denom);
    }

    private float GetPCISPHDelta(float h, float mass, float dt)
    {
        if (h != _deltaH || mass != _deltaMass || dt != _deltaDt)
        {
            _cachedDelta = ComputePCISPHDelta(h, mass, dt);
            _deltaH = h; _deltaMass = mass; _deltaDt = dt;
        }
        return _cachedDelta;
    }

    private void FixedUpdate()
    {
        int groupsPadded = Mathf.CeilToInt((float)paddedParticles / THREADS);
        int groupsPhysics = Mathf.CeilToInt((float)totalParticles / THREADS);
        int groupsCells = Mathf.CeilToInt((float)_gridCellCount / THREADS);

        // Dynamic parameters
        float spacing = 2f * particleRadius;
        float hTarget = 2.0f * spacing;
        float hValue = supportRadius > 0f ? supportRadius : hTarget;
        if (clampSupportRadius) hValue = Mathf.Clamp(hValue, 1.8f * spacing, 2.4f * spacing);

        float mass = autoMassFromRadius ? restingDensity * spacing * spacing * spacing : particleMass;

        UploadKernelConstants(hValue, mass);
        UploadTunables();

        if (collisionSphere)
        {
            shader.SetVector("spherePosition", collisionSphere.position);
            float r = 0.5f * Mathf.Max(
                Mathf.Abs(collisionSphere.lossyScale.x),
                Mathf.Abs(collisionSphere.lossyScale.y),
                Mathf.Abs(collisionSphere.lossyScale.z));
            shader.SetFloat("sphereRadius", r);
        }

        // Substepping. WCSPH's Tait EOS uses c0 = stiffness as the speed of sound, so it needs
        // sound-speed CFL substepping (dt_sub <= cflLambda * h / c0) or it blows up at any real
        // stiffness. PCISPH is stable at the full step via its iterative solve, so it just uses
        // the manual substep count.
        int nSub = Mathf.Max(1, substeps);
        if (solverMode == SolverMode.WCSPH && autoSubsteps)
        {
            float c0 = Mathf.Max(stiffness, 1e-3f);
            float dtCFL = cflLambda * hValue / c0;
            nSub = Mathf.Clamp(Mathf.CeilToInt(timeStep / Mathf.Max(dtCFL, 1e-6f)), 1, maxSubsteps);
        }
        float dtSub = timeStep / nSub;

        if (solverMode == SolverMode.PCISPH)
            shader.SetFloat("_pcisphDelta", GetPCISPHDelta(hValue, mass, dtSub));

        for (int step = 0; step < nSub; ++step)
        {
            shader.SetFloat("timeStep", dtSub);

            // 1. Clear grid ranges
            shader.Dispatch(clearCellRangesKernel, groupsCells, 1, 1);

            // 2. Build sort keys (cell index per particle)
            shader.Dispatch(initSortKeysKernel, groupsPadded, 1, 1);

            // 3. Sort indices by cell
            SortParticles();

            // 4. Cell start/end ranges + reorder into contiguous sorted buffer
            shader.Dispatch(calculateCellStartEndKernel, groupsCells, 1, 1);
            shader.Dispatch(reorderParticlesKernel, groupsPadded, 1, 1);

            // 5. SPH passes (solver-specific)
            if (solverMode == SolverMode.PCISPH)
            {
                // Non-pressure forces + iterative pressure projection.
                shader.Dispatch(pciDensityKernel, groupsPhysics, 1, 1);
                // Normals feed surface tension and the diffuse wave-crest term; compute for either.
                if (enableSurfaceTension || enableDiffuse)
                    shader.Dispatch(computeNormalsKernel, groupsPhysics, 1, 1);
                shader.Dispatch(pciNonPressureKernel, groupsPhysics, 1, 1);
                shader.Dispatch(pciPredictKernel, groupsPhysics, 1, 1);

                for (int it = 0; it < solverIterations; ++it)
                {
                    shader.Dispatch(pciPressureUpdateKernel, groupsPhysics, 1, 1);
                    shader.Dispatch(pciPressureAccelKernel, groupsPhysics, 1, 1);
                    shader.Dispatch(pciApplyAccelKernel, groupsPhysics, 1, 1);
                }

                shader.Dispatch(pciPressureForceKernel, groupsPhysics, 1, 1);
            }
            else
            {
                // WCSPH: Tait EOS pressure in one pass.
                shader.Dispatch(densityPressureKernel, groupsPhysics, 1, 1);
                shader.Dispatch(computeNormalsKernel, groupsPhysics, 1, 1);
                shader.Dispatch(computeForceKernel, groupsPhysics, 1, 1);
            }

            if (enableVorticity)
            {
                shader.Dispatch(computeVorticityKernel, groupsPhysics, 1, 1);
                shader.Dispatch(applyVorticityKernel, groupsPhysics, 1, 1);
            }

            if (enableXSPH && xsphAlpha > 0f)
            {
                shader.Dispatch(computeXsphKernel, groupsPhysics, 1, 1);
                shader.Dispatch(applyXsphKernel, groupsPhysics, 1, 1);
            }

            // 6. Integrate (reads sorted, writes master) — shared by both solvers
            shader.Dispatch(integrateKernel, groupsPhysics, 1, 1);
        }

        // Diffuse particles: once per FixedUpdate, reusing the last substep's grid (still bound).
        // Advect/age the existing pool first, then spawn new markers from the current fluid state.
        if (enableDiffuse && _diffuseReady)
        {
            UploadDiffuseTunables(timeStep);
            // Monotonic per-dispatch seed: Time.frameCount would repeat when multiple
            // FixedUpdates run in one render frame, biasing spawn offsets.
            shader.SetInt("_diffuseSeed", ++_diffuseFrame);
            int groupsDiffuse = Mathf.CeilToInt((float)_actualMaxDiffuse / THREADS);
            shader.Dispatch(_diffuseAdvectKernel, groupsDiffuse, 1, 1);
            shader.Dispatch(_diffuseGenerateKernel, groupsPhysics, 1, 1);
        }

        if (logDiagnostics && (++_logCounter % logInterval == 0))
            LogDiagnostics(hValue, mass, dtSub, nSub);
    }

    // Synchronous readback of the master buffer -> avg density / velocity / bbox.
    // Editor-only debugging: rho ≈ restingDensity and V ≈ 0 means a settled pool;
    // rho blowing up or V large/NaN means the solve is unstable.
    private void LogDiagnostics(float h, float mass, float dtSub, int nSub)
    {
        // A script recompile during Play can momentarily leave _readback / totalParticles out of
        // sync with the actual buffer, which made GetData throw "Bad indices/count argument".
        // Clamp the readback to what both the buffer and the array actually hold.
        if (_particlesBuffer == null || !_particlesBuffer.IsValid()) return;
        int count = Mathf.Min(totalParticles, _particlesBuffer.count);
        if (_readback == null || _readback.Length < count) _readback = new Particle[count];
        _particlesBuffer.GetData(_readback, 0, 0, count);

        float rhoSum = 0f, rhoMax = 0f, vSum = 0f, vMax = 0f, pMax = 0f;
        int nanCount = 0;
        Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (int i = 0; i < count; i++)
        {
            Particle p = _readback[i];
            float v = p.velocity.magnitude;
            if (float.IsNaN(p.density) || float.IsNaN(v) || float.IsNaN(p.position.x)) { nanCount++; continue; }
            rhoSum += p.density; rhoMax = Mathf.Max(rhoMax, p.density);
            vSum += v; vMax = Mathf.Max(vMax, v);
            pMax = Mathf.Max(pMax, p.pressure);
            lo = Vector3.Min(lo, p.position); hi = Vector3.Max(hi, p.position);
        }

        int valid = Mathf.Max(1, count - nanCount);
        Debug.Log($"[SPH {solverMode}] n={count} h={h:F3} m={mass:F4} dtSub={dtSub:F5} nSub={nSub} | " +
                  $"rho avg={rhoSum / valid:F1} max={rhoMax:F1} (rho0={restingDensity}) | " +
                  $"V avg={vSum / valid:F3} max={vMax:F3} | Pmax={pMax:F0} | " +
                  $"bbox={(hi - lo):F2} | NaN={nanCount}");
    }

    private void SortParticles()
    {
        int groups = Mathf.CeilToInt((float)paddedParticles / THREADS);
        for (var dim = 2; dim <= paddedParticles; dim <<= 1)
        {
            shader.SetInt("dim", dim);
            for (var block = dim >> 1; block > 0; block >>= 1)
            {
                shader.SetInt("block", block);
                shader.Dispatch(sortKernel, groups, 1, 1);
            }
        }
    }

    private static readonly int SizeProperty = Shader.PropertyToID("_size");
    private static readonly int ParticlesBufferProperty = Shader.PropertyToID("_particlesBuffer");
    private static readonly int DiffuseBufferProperty = Shader.PropertyToID("_diffuseBuffer");
    private static readonly int ColorModeProperty = Shader.PropertyToID("_ColorMode");
    private static readonly int ColorMinProperty = Shader.PropertyToID("_ColorMin");
    private static readonly int ColorMaxProperty = Shader.PropertyToID("_ColorMax");
    private static readonly int EmissionProperty = Shader.PropertyToID("_Emission");

    private void Update()
    {
        // Render the master buffer, which Integrate writes back to.
        material.SetFloat(SizeProperty, particleRenderSize);
        material.SetBuffer(ParticlesBufferProperty, _particlesBuffer);

        // Live particle coloring (uniform / speed / density gradient). Each mode keeps its own
        // range; the density range is a fraction of rho0 so it adapts to any restingDensity.
        material.SetFloat(ColorModeProperty, (float)(int)particleColorMode);
        if (particleColorMode == ColorMode.Density)
        {
            material.SetFloat(ColorMinProperty, densityColorMinFrac * restingDensity);
            material.SetFloat(ColorMaxProperty, densityColorMaxFrac * restingDensity);
        }
        else
        {
            material.SetFloat(ColorMinProperty, speedColorMin);
            material.SetFloat(ColorMaxProperty, speedColorMax);
        }
        // Emission only in the gradient modes so Uniform keeps the original flat look (fix #2).
        material.SetFloat(EmissionProperty, particleColorMode == ColorMode.Uniform ? 0f : colorEmission);

        if (showSpheres)
        {
            Graphics.DrawMeshInstancedIndirect(
                particleMesh,
                0,
                material,
                new Bounds(spawnCenter, boxSize),
                _argsBuffer,
                castShadows: UnityEngine.Rendering.ShadowCastingMode.Off);
        }

        // Diffuse foam/spray/bubbles. Inactive slots collapse to zero size in the shader.
        if (enableDiffuse && _diffuseReady && diffuseMaterial != null)
        {
            diffuseMaterial.SetFloat(SizeProperty, diffuseRenderSize * particleRadius);
            diffuseMaterial.SetBuffer(DiffuseBufferProperty, _diffuseBuffer);
            Graphics.DrawMeshInstancedIndirect(
                particleMesh,
                0,
                diffuseMaterial,
                new Bounds(spawnCenter, boxSize),
                _diffuseArgsBuffer,
                castShadows: UnityEngine.Rendering.ShadowCastingMode.Off);
        }
    }

    private void OnDestroy()
    {
        _argsBuffer?.Release();
        _particlesBuffer?.Release();
        _sortedParticlesBuffer?.Release();
        _particleIndices?.Release();
        _particleNormalsBuffer?.Release();
        _xsphDelta?.Release();
        _vorticity?.Release();
        _sortKeys?.Release();
        _cellStarts?.Release();
        _cellEnds?.Release();
        _predPos?.Release();
        _predVel?.Release();
        _predAccel?.Release();
        _diffuseBuffer?.Release();
        _diffuseCount?.Release();
        _diffuseArgsBuffer?.Release();
        _boundaryPositions?.Release();
        _boundaryPsi?.Release();
        _boundaryCellStarts?.Release();
        _boundaryCellEnds?.Release();
    }
}
