using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine.Pool;

[System.Serializable]

// The struct size needs to be a multiple of 16 for compute shaders in many cases.
// float (4) + float (4) + Vector3 (12) + Vector3 (12) + Vector3 (12) + Vector3 (12) = 56 bytes.
// Let's align to the next multiple of 16, which is 64.
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct Particles
{
    // variables you need for the governing equation
    public float pressure;
    public float density;
    public Vector3 currentForce;
    public Vector3 velocity;
    public Vector3 position;

    public float nearDensity; // additional variable for double density SPH
    public Vector3 predictedPosition; // additional variable for double density SPH
    public float padding;
}

/*
public class DoubleDensitySPH : MonoBehaviour
// monobehavior is a fundamental class that serves as the base for scripts written in C#
{
    [Header("General")]
    public Transform collisionSphere;
    public bool showSpheres = true;
    public Vector3Int numToSpawn = new Vector3Int(16, 16, 16);
    // will create a grid of particles that we specify in each axis
    private int totalParticles
    {
        get
        {
            return numToSpawn.x * numToSpawn.y * numToSpawn.z;
        }
    }
    public Vector3 boxSize = new Vector3(15, 15, 15);
    // size of our fluid bouncdary (temporary)
    public Vector3 spawnCenter;
    // where the fluid spawns
    public float particleRadius = 0.1f;
    public float spawnJitter = 0.2f;


    [Header("Particle Rendering")]
    // GPU instancing
    public Mesh particleMesh;
    public float particleRenderSize = 8f;
    public Material material;


    [Header("Compute")]
    public ComputeShader shader;
    public Particles[] particles;
    // Array that contains spawned particles


    [Header("Fluid Constants")]
    public float boundDamping = -0.3f;
    public float viscosity = -0.003f;
    public float particleMass = 1f;
    public float gasConstant = 2f;
    public float restingDensity = 1f;
    public float timeStep = 0.007f;


    // DOUBLE DENSITY RELAXATION settings (exposed)
    [Header("Double Density Relaxation (DDR)")]
    public bool enableDoubleDensityRelaxation = false;
    [Tooltip("k (stiffness) for DDR. Typical small values like 0.004")]
    public float stiffness = 0.004f;
    [Tooltip("k_near (near-stiffness) for DDR. Typical values like 0.01")]
    public float nearStiffness = 0.001f;
    [Tooltip("How many DDR iterations to run per timestep (1-4 typical)")]
    [Range(1, 8)]
    public int doubleDensityIterations = 2;

    // Private variables
    // Constructrors 
    // Compute Buffers
    private ComputeBuffer _argsBuffer;
    // Contains arguments for the GPU instanced spheres
    //private ComputeBuffer _particlesBuffer;
    public ComputeBuffer _particlesBuffer;
    // All the partices in the simulation
    private ComputeBuffer _particleIndices;
    // This is a second buffer that we can use to store particles, but we do no
    private ComputeBuffer _particleCellIndices; // This is a buffer that contains the indicies of the particles in each cell
    private ComputeBuffer _cellOffsets;
    private ComputeBuffer _displacementsBuffer;

    // Kernel IDs
    private int integrateKernel;
    private int computeForcesKernel;
    private int densityPressureKernel;
    private int hashParticlesKernel;
    private int sortKernel;
    private int calculateCellOffsetsKernel;

    private int predictPositionsKernel;
    private int doubleDensityKernel;
    private int applyDisplacementsKernel;
    private int updateVelocityKernel;



    private void OnDrawGizmos()
    {
        Gizmos.color = Color.blue;
        Gizmos.DrawWireCube(Vector3.zero, boxSize);
        //Gizmos.DrawWireCube(spawnCenter, boxSize);

        if (!Application.isPlaying)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(spawnCenter, 0.1f);
            // the details can be edited later           
        }
    }


    private void Awake()
    {
        // spawn particles
        SpawnParticleInBox();
        SetupCompute();
    }

    void SetupCompute()
    {
        // Setup args for Instanced Particle Rendering
        // We do not need negative numbers
        uint[] args = {
            particleMesh.GetIndexCount(0),
            (uint)totalParticles,
            particleMesh.GetIndexStart(0),
            particleMesh.GetBaseVertex(0),
            0
        };

        _argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        _argsBuffer.SetData(args);

        _particlesBuffer = new ComputeBuffer(totalParticles, Marshal.SizeOf<Particles>());
        _particlesBuffer.SetData(particles);

        _particleIndices = new ComputeBuffer(totalParticles, sizeof(uint));
        _particleCellIndices = new ComputeBuffer(totalParticles, sizeof(uint));
        _cellOffsets = new ComputeBuffer(totalParticles, sizeof(uint));
        _displacementsBuffer = new ComputeBuffer(totalParticles, sizeof(float) * 3);

        uint[] particleIndices = new uint[totalParticles];

        for (int i = 0; i < particleIndices.Length; i++)
        {
            particleIndices[i] = (uint)i;
        }

        _particleIndices.SetData(particleIndices);

        // --- Find all Kernels ---
        integrateKernel = shader.FindKernel("Integrate");
        computeForcesKernel = shader.FindKernel("ComputeForces");
        densityPressureKernel = shader.FindKernel("ComputeDensityPressure");
        hashParticlesKernel = shader.FindKernel("HashParticles");
        sortKernel = shader.FindKernel("BitonicSort");
        calculateCellOffsetsKernel = shader.FindKernel("CalculateCellOffsets");

        // Find the new kernels
        predictPositionsKernel = shader.FindKernel("PredictPositions");
        doubleDensityKernel = shader.FindKernel("DoubleDensityRelaxation");
        applyDisplacementsKernel = shader.FindKernel("ApplyDisplacements");
        updateVelocityKernel = shader.FindKernel("UpdateVelocity");

        // --- Set Shader Variables ---
        shader.SetInt("particleLength", totalParticles);
        shader.SetFloat("particleMass", particleMass);
        shader.SetFloat("viscosity", viscosity);
        shader.SetFloat("gasConstant", gasConstant);
        shader.SetFloat("restDensity", restingDensity);
        shader.SetFloat("boundDamping", boundDamping);
        shader.SetFloat("pi", Mathf.PI);
        shader.SetVector("boxSize", boxSize);

        shader.SetFloat("radius", particleRadius);
        shader.SetFloat("radius2", particleRadius * particleRadius);
        shader.SetFloat("radius3", particleRadius * particleRadius * particleRadius);
        shader.SetFloat("radius4", particleRadius * particleRadius * particleRadius * particleRadius);
        shader.SetFloat("radius5", particleRadius * particleRadius * particleRadius * particleRadius * particleRadius);

        // DDR defaults
        shader.SetFloat("stiffness", stiffness);
        shader.SetFloat("nearStiffness", nearStiffness);

        // bind common buffers for kernels that need them
        // Hash
        shader.SetBuffer(hashParticlesKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(hashParticlesKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(hashParticlesKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(hashParticlesKernel, "_cellOffsets", _cellOffsets);

        // Sort
        shader.SetBuffer(sortKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(sortKernel, "_particleCellIndices", _particleCellIndices);

        // Calculate Offsets
        shader.SetBuffer(calculateCellOffsetsKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(calculateCellOffsetsKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(calculateCellOffsetsKernel, "_cellOffsets", _cellOffsets);

        // DDR kernels
        shader.SetBuffer(predictPositionsKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(doubleDensityKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(doubleDensityKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(doubleDensityKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(doubleDensityKernel, "_cellOffsets", _cellOffsets);
        shader.SetBuffer(doubleDensityKernel, "_displacements", _displacementsBuffer);

        shader.SetBuffer(applyDisplacementsKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(applyDisplacementsKernel, "_displacements", _displacementsBuffer);

        shader.SetBuffer(updateVelocityKernel, "_particles", _particlesBuffer);

        // Non-DDR path buffers
        shader.SetBuffer(densityPressureKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(densityPressureKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(densityPressureKernel, "_cellOffsets", _cellOffsets);

        shader.SetBuffer(computeForcesKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(computeForcesKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(computeForcesKernel, "_cellOffsets", _cellOffsets);
        shader.SetBuffer(computeForcesKernel, "_particleCellIndices", _particleCellIndices);

        shader.SetBuffer(integrateKernel, "_particles", _particlesBuffer);
    }

    private void FixedUpdate()
    {
        int dispatchGroupCount = Mathf.Max(1, Mathf.CeilToInt(totalParticles / 256f));
        shader.SetFloat("timeStep", timeStep);

        if (collisionSphere != null)
        {
            shader.SetVector("spherePosition", collisionSphere.transform.position);
            shader.SetFloat("sphereRadius", collisionSphere.transform.localScale.x / 2f);
        }

        shader.SetVector("boxSize", boxSize);

        // --- 1. Spatial Hashing (Common to both methods) ---
        shader.SetBuffer(hashParticlesKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(hashParticlesKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(hashParticlesKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(hashParticlesKernel, "_cellOffsets", _cellOffsets);
        shader.Dispatch(hashParticlesKernel, dispatchGroupCount, 1, 1);

        // Bitonic Sort
        for (var dim = 2; dim <= totalParticles; dim <<= 1)
        {
            for (var block = dim >> 1; block > 0; block >>= 1)
            {
                shader.SetInt("block", block);
                shader.SetInt("dim", dim);
                shader.SetBuffer(sortKernel, "_particleIndices", _particleIndices);
                shader.SetBuffer(sortKernel, "_particleCellIndices", _particleCellIndices);
                shader.Dispatch(sortKernel, dispatchGroupCount, 1, 1);
            }
        }

        shader.SetBuffer(calculateCellOffsetsKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(calculateCellOffsetsKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(calculateCellOffsetsKernel, "_cellOffsets", _cellOffsets);
        shader.Dispatch(calculateCellOffsetsKernel, dispatchGroupCount, 1, 1);

        if (enableDoubleDensityRelaxation)
        {
            shader.SetBuffer(predictPositionsKernel, "_particles", _particlesBuffer);
            shader.Dispatch(predictPositionsKernel, dispatchGroupCount, 1, 1);

            // If DDR enabled, run the DDR kernel multiple times, then recompute density
            for (int i = 0; i < doubleDensityIterations; i++)
            {
                // This is Algorithm 2
                shader.SetBuffer(doubleDensityKernel, "_particles", _particlesBuffer);
                shader.SetBuffer(doubleDensityKernel, "_particleIndices", _particleIndices);
                shader.SetBuffer(doubleDensityKernel, "_particleCellIndices", _particleCellIndices);
                shader.SetBuffer(doubleDensityKernel, "_cellOffsets", _cellOffsets);
                shader.SetBuffer(doubleDensityKernel, "_displacements", _displacementsBuffer);
                shader.Dispatch(doubleDensityKernel, dispatchGroupCount, 1, 1);

                // Apply the calculated displacements
                shader.SetBuffer(applyDisplacementsKernel, "_particles", _particlesBuffer);
                shader.SetBuffer(applyDisplacementsKernel, "_displacements", _displacementsBuffer);
                shader.Dispatch(applyDisplacementsKernel, dispatchGroupCount, 1, 1);
            }

            shader.SetBuffer(computeForcesKernel, "_particles", _particlesBuffer);
            shader.SetBuffer(computeForcesKernel, "_particleIndices", _particleIndices);
            shader.SetBuffer(computeForcesKernel, "_cellOffsets", _cellOffsets);
            shader.SetBuffer(computeForcesKernel, "_particleCellIndices", _particleCellIndices);
            shader.Dispatch(computeForcesKernel, dispatchGroupCount, 1, 1);

            shader.SetBuffer(updateVelocityKernel, "_particles", _particlesBuffer);
            shader.Dispatch(updateVelocityKernel, dispatchGroupCount, 1, 1);
        }
        else
        {
            shader.SetBuffer(densityPressureKernel, "_particles", _particlesBuffer);
            shader.SetBuffer(densityPressureKernel, "_particleIndices", _particleIndices);
            shader.SetBuffer(densityPressureKernel, "_cellOffsets", _cellOffsets);
            shader.Dispatch(densityPressureKernel, dispatchGroupCount, 1, 1);

            shader.SetBuffer(computeForcesKernel, "_particles", _particlesBuffer);
            shader.SetBuffer(computeForcesKernel, "_particleIndices", _particleIndices);
            shader.SetBuffer(computeForcesKernel, "_cellOffsets", _cellOffsets);
            shader.SetBuffer(computeForcesKernel, "_particleCellIndices", _particleCellIndices);
            shader.Dispatch(computeForcesKernel, dispatchGroupCount, 1, 1);

            shader.SetBuffer(integrateKernel, "_particles", _particlesBuffer);
            shader.Dispatch(integrateKernel, dispatchGroupCount, 1, 1);
        }
    }

    private void SpawnParticleInBox()
    {
        Vector3 spawnPoint = spawnCenter;
        List<Particles> _particles = new List<Particles>();

        for (int x = 0; x < numToSpawn.x; x++)
        {
            for (int y = 0; y < numToSpawn.y; y++)
            {
                for (int z = 0; z < numToSpawn.z; z++)
                {
                    Vector3 spawnPosition = spawnPoint + new Vector3(x * particleRadius * 2.1f, y * particleRadius * 2.1f, z * particleRadius * 2.1f);
                    spawnPosition += Random.onUnitSphere * particleRadius * spawnJitter;

                    Particles p = new Particles { position = spawnPosition };

                    _particles.Add(p);
                }
            }
        }

        particles = _particles.ToArray();
    }

    private static readonly int SizeProperty = Shader.PropertyToID("_size");
    private static readonly int ParticlesBufferProperty = Shader.PropertyToID("_particlesBuffer");

    private void Update()
    {
        if (showSpheres)
        {
            // Render the particles
            material.SetFloat(SizeProperty, particleRenderSize);
            material.SetBuffer(ParticlesBufferProperty, _particlesBuffer);
            Graphics.DrawMeshInstancedIndirect(
                particleMesh,
                0,
                material,
                new Bounds(Vector3.zero, boxSize),
                _argsBuffer,
                castShadows: UnityEngine.Rendering.ShadowCastingMode.Off);
            // to render instance meshes
        }
    }

    private void OnDestroy()
    {
        _argsBuffer?.Release();
        _particlesBuffer?.Release();
        _particleIndices?.Release();
        _particleCellIndices?.Release();
        _cellOffsets?.Release();
        _displacementsBuffer?.Release();
    }

}
*/


public class DoubleDensitySPH : MonoBehaviour
// monobehavior is a fundamental class that serves as the base for scripts written in C#
{
    [Header("Double Density Relaxation (DDR)")]
    public bool enableDoubleDensity = false;
    [Tooltip("The number of relaxation iterations per frame. 1-4 is typical.")]
    [Range(1, 8)]
    public int solverIterations = 2;
    [Tooltip("Stiffness constant (k in the paper). Typical value: 0.004")]
    public float stiffness = 0.004f;
    [Tooltip("Near-stiffness constant (k_near in the paper). Typical value: 0.01")]
    public float nearStiffness = 0.01f;

    [Header("General")]
    public Transform collisionSphere;
    public bool showSpheres = true;
    public Vector3Int numToSpawn = new Vector3Int(10, 10, 10);
    // will create a grid of particles that we specify in each axis
    private int totalParticles
    {
        get
        {
            return numToSpawn.x * numToSpawn.y * numToSpawn.z;
        }
    }
    public Vector3 boxSize = new Vector3(10, 10, 10);
    // size of our fluid bouncdary (temporary)
    public Vector3 spawnCenter;
    // where the fluid spawns
    public float particleRadius = 0.1f;
    public float spawnJitter = 0.2f;

    [Header("Particle Rendering")]
    // GPU instancing
    public Mesh particleMesh;
    public float particleRenderSize = 8f;
    public Material material;

    [Header("Compute")]
    public ComputeShader shader;
    public Particles[] particles;
    // Array that contains spawned particles

    [Header("Fluid Constants")]
    public float boundDamping = -0.3f;
    public float viscosity = -0.003f;
    public float particleMass = 1f;
    public float gasConstant = 2f;
    public float restingDensity = 1f;
    public float timeStep = 0.007f;

    [Header("Grid/Hashing")]
    public int hashTableSize = 8192;


    // Private variables
    // Constructrors 
    private ComputeBuffer _argsBuffer;
    // Contains arguments for the GPU instanced spheres
    //private ComputeBuffer _particlesBuffer;
    public ComputeBuffer _particlesBuffer;
    // All the partices in the simulation

    private ComputeBuffer _particleIndices;
    // This is a second buffer that we can use to store particles, but we do no
    private ComputeBuffer _particleCellIndices; // This is a buffer that contains the indicies of the particles in each cell
    private ComputeBuffer _cellOffsets;
    private ComputeBuffer _displacementsBuffer;
   
    private int integrateKernel;
    private int computeForceKernel;
    private int densityPressureKernel;
    private int hashParticlesKernel;
    private int sortKernel;
    private int calculateCellOffsetsKernel;

    private int predictPositionsKernel;
    private int doubleDensityKernel;
    private int applyDisplacementsKernel;
    private int updateVelocityKernel;
    private uint[] _cellClear;

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.blue;
        Gizmos.DrawWireCube(Vector3.zero, boxSize);
        //Gizmos.DrawWireCube(spawnCenter, boxSize);

        if (!Application.isPlaying)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(spawnCenter, 0.1f);
            // the details can be edited later           
        }
    }


    private void Awake()
    {
        // spawn particles
        SpawnParticleInBox();

        // Setup args for Instanced Particle Rendering
        // We do not need negative numbers
        uint[] args = {
            particleMesh.GetIndexCount(0),
            (uint)totalParticles,
            particleMesh.GetIndexStart(0),
            particleMesh.GetBaseVertex(0),
            0
        };

        _argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        _argsBuffer.SetData(args);

        _particlesBuffer = new ComputeBuffer(totalParticles, 64);
        _particlesBuffer.SetData(particles);

        _particleIndices = new ComputeBuffer(totalParticles, sizeof(uint));
        _particleCellIndices = new ComputeBuffer(totalParticles, sizeof(uint));
        _cellOffsets = new ComputeBuffer(hashTableSize, sizeof(uint));
        _cellClear = new uint[hashTableSize];
        _displacementsBuffer = new ComputeBuffer(totalParticles, sizeof(float) * 3);

        uint[] particleIndices = new uint[totalParticles];

        for (int i = 0; i < particleIndices.Length; i++)
        {
            particleIndices[i] = (uint)i; 
        }

        _particleIndices.SetData(particleIndices);

        SetupComputeBuffers();
    }

    private void SetupComputeBuffers()
    {
        integrateKernel = shader.FindKernel("Integrate");
        computeForceKernel = shader.FindKernel("ComputeForces");
        densityPressureKernel = shader.FindKernel("ComputeDensityPressure");
        hashParticlesKernel = shader.FindKernel("HashParticles");
        sortKernel = shader.FindKernel("BitonicSort");
        calculateCellOffsetsKernel = shader.FindKernel("CalculateCellOffsets");

        predictPositionsKernel = shader.FindKernel("PredictPositions");
        doubleDensityKernel = shader.FindKernel("DoubleDensityRelaxation");
        applyDisplacementsKernel = shader.FindKernel("ApplyDisplacements");
        updateVelocityKernel = shader.FindKernel("UpdateVelocity");

        shader.SetInt("particleLength", totalParticles);
        shader.SetFloat("particleMass", particleMass);
        shader.SetFloat("viscosity", viscosity);
        shader.SetFloat("gasConstant", gasConstant);
        shader.SetFloat("restDensity", restingDensity);
        shader.SetFloat("boundDamping", boundDamping);
        shader.SetFloat("pi", Mathf.PI);
        shader.SetVector("boxSize", boxSize);
        shader.SetFloat("stiffness", stiffness);
        shader.SetFloat("nearStiffness", nearStiffness);
        shader.SetInt("_hashTableSize", hashTableSize);

        shader.SetFloat("radius", particleRadius);
        shader.SetFloat("radius2", particleRadius * particleRadius);
        shader.SetFloat("radius3", particleRadius * particleRadius * particleRadius);
        shader.SetFloat("radius4", particleRadius * particleRadius * particleRadius * particleRadius);
        shader.SetFloat("radius5", particleRadius * particleRadius * particleRadius * particleRadius * particleRadius);

        shader.SetBuffer(integrateKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(computeForceKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(densityPressureKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(hashParticlesKernel, "_particles", _particlesBuffer);

        shader.SetBuffer(computeForceKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(densityPressureKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(hashParticlesKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(sortKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(calculateCellOffsetsKernel, "_particleIndices", _particleIndices);

        shader.SetBuffer(computeForceKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(densityPressureKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(hashParticlesKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(sortKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(calculateCellOffsetsKernel, "_particleCellIndices", _particleCellIndices);

        shader.SetBuffer(computeForceKernel, "_cellOffsets", _cellOffsets);
        shader.SetBuffer(hashParticlesKernel, "_cellOffsets", _cellOffsets);
        shader.SetBuffer(densityPressureKernel, "_cellOffsets", _cellOffsets);
        shader.SetBuffer(calculateCellOffsetsKernel, "_cellOffsets", _cellOffsets);

        shader.SetBuffer(predictPositionsKernel, "_particles", _particlesBuffer);

        shader.SetBuffer(doubleDensityKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(doubleDensityKernel, "_particleIndices", _particleIndices);
        shader.SetBuffer(doubleDensityKernel, "_particleCellIndices", _particleCellIndices);
        shader.SetBuffer(doubleDensityKernel, "_cellOffsets", _cellOffsets);
        shader.SetBuffer(doubleDensityKernel, "_displacements", _displacementsBuffer);
        
        shader.SetBuffer(applyDisplacementsKernel, "_particles", _particlesBuffer);
        shader.SetBuffer(applyDisplacementsKernel, "_displacements", _displacementsBuffer);

        shader.SetBuffer(updateVelocityKernel, "_particles", _particlesBuffer);
    }

    private void SortParticles()
    {
        for (var dim = 2; dim <= totalParticles; dim <<= 1)
        {
            shader.SetInt("dim", dim);
            for (var block = dim >> 1; block > 0; block >>= 1) {
                shader.SetInt("block", block);
                int dispatch = Mathf.CeilToInt(totalParticles / 256f);
                shader.Dispatch(sortKernel, dispatch, 1, 1);
            } 
        }
    }

    private void FixedUpdate()
    {
        _cellOffsets.SetData(_cellClear);
        int dispatchCount = Mathf.CeilToInt((float)totalParticles / 256);

        shader.SetVector("boxSize", boxSize);
        shader.SetFloat("timeStep", timeStep);
        shader.SetVector("spherePosition", collisionSphere.transform.position);
        shader.SetFloat("sphereRadius", collisionSphere.transform.localScale.x / 2);

        shader.Dispatch(hashParticlesKernel, dispatchCount, 1, 1);
        SortParticles();
        shader.Dispatch(calculateCellOffsetsKernel, dispatchCount, 1, 1);

        if (enableDoubleDensity)
        {
             // 1. Apply viscosity first
            shader.Dispatch(computeForceKernel, dispatchCount, 1, 1);
            // 2. Predict positions using the now-dampened velocity.
            shader.Dispatch(predictPositionsKernel, dispatchCount, 1, 1);
            // 3. Run the relaxation solver.
            for (int i = 0; i < solverIterations; i++)
            {
                shader.Dispatch(doubleDensityKernel, dispatchCount, 1, 1);
                shader.Dispatch(applyDisplacementsKernel, dispatchCount, 1, 1);
            }
            // 4. Update the final velocity.
            shader.Dispatch(updateVelocityKernel, dispatchCount, 1, 1);
        }
        else
        {
            shader.Dispatch(densityPressureKernel, dispatchCount, 1, 1);
            shader.Dispatch(computeForceKernel, dispatchCount, 1, 1);
            shader.Dispatch(integrateKernel, dispatchCount, 1, 1);
        }
    }
    private void SpawnParticleInBox()
    {
        Vector3 spawnPoint = spawnCenter;
        List<Particles> _particles = new List<Particles>();

        for (int x = 0; x < numToSpawn.x; x++)
        {
            for (int y = 0; y < numToSpawn.y; y++)
            {
                for (int z = 0; z < numToSpawn.z; z++)
                {
                    Vector3 spawnPosition = spawnPoint + new Vector3(x * particleRadius * 2, y * particleRadius * 2, z * particleRadius * 2);
                    spawnPosition += Random.onUnitSphere * particleRadius * spawnJitter;

                    Particles p = new Particles { position = spawnPosition };

                    _particles.Add(p);
                }
            }
        }

        particles = _particles.ToArray();
    }

    private static readonly int SizeProperty = Shader.PropertyToID("_size");
    private static readonly int ParticlesBufferProperty = Shader.PropertyToID("_particlesBuffer");

    private void Update()
    {
        // Render the particles
        material.SetFloat(SizeProperty, particleRenderSize);
        material.SetBuffer(ParticlesBufferProperty, _particlesBuffer);

        if (showSpheres)
        {
            Graphics.DrawMeshInstancedIndirect(
                particleMesh,
                0,
                material,
                new Bounds(Vector3.zero, boxSize),
                _argsBuffer,
                castShadows: UnityEngine.Rendering.ShadowCastingMode.Off);
            // to render instance meshes
        }
    }

}


