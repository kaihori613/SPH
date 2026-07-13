using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine.Pool;

[System.Serializable]
[StructLayout(LayoutKind.Sequential, Size = 72)]
// size = 44 bytes is an arbituary number
public struct newParticle
{
    // variables you need for the governing equation
    public float pressure;
    public float density;
    public Vector3 currentForce;
    public Vector3 velocity;
    public Vector3 position;
    // NEW: PCISPH requires predicted velocity and position
    public Vector3 predictedVelocity;
    public Vector3 predictedPosition;
    private float padding;
  
}

public class PCISPH : MonoBehaviour
// monobehavior is a fundamental class that serves as the base for scripts written in C#
{
    [Header("General")]
    public Transform collisionSphere;
    public bool showSpheres = true;
    public Vector3Int numToSpawn = new Vector3Int(32, 32, 32);
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
    public newParticle[] particles;
    // Array that contains spawned particles

    // --- CHANGED: PCISPH Solver Settings ---
    [Header("PCISPH Solver")]
    [Range(1, 10)]
    public int solverIterations = 3;
    private float pcisphDelta; // The 'δ' scaling factor from the paper

    [Header("Fluid Constants")]
    public float boundDamping = -0.3f;
    public float viscosity = 0.05f;
    public float particleMass = 1f;
    public float restingDensity = 1f;
    public float timeStep = 0.007f;
    public float supportRadius = 0.25f; // kernel radius 'h';

    [Header("Grid/Hashing")]
    public int hashTableSize = 32771;

    [Header("Surface Tension")]
    public bool enableSurfaceTension = true; 
    public float cohesionAlpha = 1.0f;
    public float curvatureGamma = 1.0f;
    public float adhesionBeta = 0.0f;

    [Header("XSPH Viscosity")]
    public float xsphAlpha = 0.05f;
    public bool enableXSPH = true;

    [Header("Vorticity")]
    public bool enableVorticity = true;
    public float vorticityEps = 0.1f;
    
    // Private variables
    private ComputeBuffer _argsBuffer;
    public ComputeBuffer _particlesBuffer;
    private ComputeBuffer _particleIndices;
    private ComputeBuffer _particleCellIndices; 
    private ComputeBuffer _cellOffsets;
    private ComputeBuffer _particleNormalsBuffer;
    private ComputeBuffer _xsphDelta;
    private ComputeBuffer _vorticity;
    private uint[] _cellClear;
    
    private int hashKernel, sortKernel, offsetKernel;
    private int computeNormalsKernel;
    private int nonPressureForcesKernel;
    private int predictStepKernel;
    private int pressureUpdateKernel;
    private int computePressureForceKernel;
    private int finalIntegrateKernel;
    private int computeXsphKernel, applyXsphKernel, computeVorticityKernel, applyVorticityKernel, applyPressurePredictionKernel; 

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.blue;
        Gizmos.DrawWireCube(Vector3.zero, boxSize);

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

        uint[] args = {
            particleMesh.GetIndexCount(0),
            (uint)totalParticles,
            particleMesh.GetIndexStart(0),
            particleMesh.GetBaseVertex(0),
            0
        };

        _argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        _argsBuffer.SetData(args);

        _particlesBuffer = new ComputeBuffer(totalParticles, 72);
        _particlesBuffer.SetData(particles);
        _particleIndices = new ComputeBuffer(totalParticles, sizeof(uint));
        _particleCellIndices = new ComputeBuffer(totalParticles, sizeof(uint));
        _particleNormalsBuffer = new ComputeBuffer(totalParticles, sizeof(float)*3); // 3 floats for normal vector
        _xsphDelta = new ComputeBuffer(totalParticles, sizeof(float) * 3);
        _vorticity = new ComputeBuffer(totalParticles, sizeof(float) * 3);

        _cellOffsets = new ComputeBuffer(hashTableSize, sizeof(uint));
        _cellClear = new uint[hashTableSize];
        
        for (int i = 0; i < hashTableSize; i++)
        {
            _cellClear[i] = 99999999;
        }

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
        hashKernel = shader.FindKernel("HashParticles");
        sortKernel = shader.FindKernel("BitonicSort");
        offsetKernel = shader.FindKernel("CalculateCellOffsets");
        computeNormalsKernel = shader.FindKernel("ComputeNormals");
        nonPressureForcesKernel = shader.FindKernel("NonPressureForces");
        predictStepKernel = shader.FindKernel("PredictStep");
        pressureUpdateKernel = shader.FindKernel("PressureUpdate");
        computePressureForceKernel = shader.FindKernel("ComputePressureForce");
        finalIntegrateKernel = shader.FindKernel("FinalIntegrate");

        computeXsphKernel = shader.FindKernel("ComputeXSPH");
        applyXsphKernel = shader.FindKernel("ApplyXSPH");
        computeVorticityKernel = shader.FindKernel("ComputeVorticity");
        applyVorticityKernel = shader.FindKernel("ApplyVorticityConfinement");
        applyPressurePredictionKernel = shader.FindKernel("ApplyPressurePrediction");

        shader.SetInt("particleLength", totalParticles);
        shader.SetInt("_hashTableSize", hashTableSize);
        shader.SetFloat("particleMass", particleMass);
        shader.SetFloat("viscosity", viscosity);
        shader.SetFloat("restDensity", restingDensity);
        shader.SetFloat("boundDamping", boundDamping);
        shader.SetFloat("pi", Mathf.PI);
        shader.SetVector("boxSize", boxSize);
        shader.SetFloat("particleRadius", particleRadius);

        shader.SetInt("enableSurfaceTension", enableSurfaceTension ? 1 : 0);
        shader.SetFloat("cohesionAlpha", cohesionAlpha);
        shader.SetFloat("curvatureGamma", curvatureGamma);
        shader.SetInt("enableXSPH", enableXSPH ? 1 : 0);
        shader.SetFloat("xsphAlpha", xsphAlpha);
        shader.SetInt("enableVorticity", enableVorticity ? 1 : 0);
        shader.SetFloat("vorticityEps", vorticityEps);

        float h = supportRadius;
        shader.SetFloat("h", h);
        shader.SetFloat("h2", h * h);
        float h3 = h * h * h;
        float h6 = h3 * h3;
        float h9 = h6 * h3;
        shader.SetFloat("invH", 1f / h);
        shader.SetFloat("poly6C", 315f / (64f * Mathf.PI * h9));
        shader.SetFloat("kSpikyGrad", -45f / (Mathf.PI * h6));
        shader.SetFloat("h3", h3);
        shader.SetFloat("h6", h6);

        pcisphDelta = CalculatePCISPHDelta(h);
        pcisphDelta *= 0.5f;
        shader.SetFloat("_pcisphDelta", pcisphDelta);

        int[] allKernels = { hashKernel, sortKernel, offsetKernel, computeNormalsKernel, nonPressureForcesKernel, predictStepKernel, 
                             pressureUpdateKernel, computePressureForceKernel, finalIntegrateKernel, computeXsphKernel, applyXsphKernel, 
                             computeVorticityKernel, applyVorticityKernel, applyPressurePredictionKernel};

        foreach (var kernel in allKernels)
        {
            shader.SetBuffer(kernel, "_particles", _particlesBuffer);
            shader.SetBuffer(kernel, "_particleIndices", _particleIndices);
            shader.SetBuffer(kernel, "_particleCellIndices", _particleCellIndices);
            shader.SetBuffer(kernel, "_cellOffsets", _cellOffsets);
        }                     

        shader.SetBuffer(computeNormalsKernel, "_particleNormals", _particleNormalsBuffer);
        shader.SetBuffer(nonPressureForcesKernel, "_particleNormals", _particleNormalsBuffer);
        shader.SetBuffer(computeXsphKernel, "_xsphDelta", _xsphDelta);
        shader.SetBuffer(applyXsphKernel, "_xsphDelta", _xsphDelta);
        shader.SetBuffer(computeVorticityKernel, "_vorticity", _vorticity);
        shader.SetBuffer(applyVorticityKernel, "_vorticity", _vorticity);   
    }

    private float CalculatePCISPHDelta(float h)
    {
        Vector3 sum_grad_w = Vector3.zero;
        float sum_grad_w_dot_grad_w = 0f;

        int numSamples = 1000;
        for (int i = 0; i < numSamples; i++)
        {
            Vector3 p = Random.insideUnitSphere * h;
            float dist = p.magnitude;
            if (dist > 0 && dist < h)
            {
                // Spiky kernel gradient
                float invDist = 1.0f / dist;
                float term = h - dist;
                Vector3 grad_w = (-45f / (Mathf.PI * Mathf.Pow(h, 6)) * term * term) * (p * invDist);
                sum_grad_w += grad_w;
                sum_grad_w_dot_grad_w += Vector3.Dot(grad_w, grad_w);
            }
        }
        float beta = 2f * Mathf.Pow(timeStep * particleMass, 2) / Mathf.Pow(restingDensity, 2);
        float denominator = -Vector3.Dot(sum_grad_w, sum_grad_w) - sum_grad_w_dot_grad_w;

        if (Mathf.Abs(denominator) < 1e-9) return 0;
        
        return -1f / (beta * denominator);
    }

    private void SortParticles()
    {
        int groups = Mathf.CeilToInt((float)totalParticles / 256f);
        for (var dim = 2; dim <= totalParticles; dim <<= 1)
        {
            shader.SetInt("dim", dim);
            for (var block = dim >> 1; block > 0; block >>= 1)
            {
                shader.SetInt("block", block);
                shader.Dispatch(sortKernel, groups, 1, 1);
            }
        }
    }

    private void FixedUpdate()
    {
        const int THREADS = 256;
        int groups = Mathf.CeilToInt((float)totalParticles / THREADS);

        shader.SetVector("boxSize", boxSize);
        shader.SetFloat("timeStep", timeStep);
        shader.SetVector("spherePosition", collisionSphere.transform.position);
        shader.SetFloat("sphereRadius", collisionSphere.transform.localScale.x / 2);

        // 1. Spatial Hashing & Sorting
        _cellOffsets.SetData(_cellClear);
        shader.Dispatch(hashKernel, groups, 1, 1);
        SortParticles();
        shader.Dispatch(offsetKernel, groups, 1, 1);

        // 2. Compute Normals (needed for surface tension)
        if (enableSurfaceTension)
        {
            shader.Dispatch(computeNormalsKernel, groups, 1, 1);
        }

        // 3. Compute non-pressure forces (viscosity, surface tension, gravity)
        shader.Dispatch(nonPressureForcesKernel, groups, 1, 1);
      
        // 4. Predict future velocities and positions
        shader.Dispatch(predictStepKernel, groups, 1, 1);

        // 5. PCISPH Pressure Correction Loop
        for (int it = 0; it < solverIterations; ++it)
        {
            shader.Dispatch(pressureUpdateKernel, groups, 1, 1);
            shader.Dispatch(applyPressurePredictionKernel, groups, 1, 1); 
        }

        // 6. Compute final pressure force from accumulated pressure
        shader.Dispatch(computePressureForceKernel, groups, 1, 1);

        // 7. Optional Forces (Vorticity)
        if (enableVorticity)
        {
            shader.Dispatch(computeVorticityKernel, groups, 1, 1);
            shader.Dispatch(applyVorticityKernel, groups, 1, 1);
        }

        // 8. Final Integration
        shader.Dispatch(finalIntegrateKernel, groups, 1, 1);
        
        // 9. Optional Velocity Correction (XSPH)
        if (enableXSPH && xsphAlpha > 0f)
        {
            shader.Dispatch(computeXsphKernel, groups, 1, 1);
            shader.Dispatch(applyXsphKernel, groups, 1, 1);
        }
    }

    private void SpawnParticleInBox()
    {
        Vector3 spawnPoint = spawnCenter;
        List<newParticle> _particles = new List<newParticle>();

        for (int x = 0; x < numToSpawn.x; x++){
            for (int y = 0; y < numToSpawn.y; y++){
                for (int z = 0; z < numToSpawn.z; z++){
                    Vector3 spawnPosition = spawnPoint + new Vector3(x * particleRadius * 2, y * particleRadius * 2, z * particleRadius * 2);
                    spawnPosition += Random.onUnitSphere * particleRadius * spawnJitter;

                    newParticle p = new newParticle
                    {
                        position = spawnPosition,
                        velocity = Vector3.zero,
                        currentForce = Vector3.zero,
                        density = restingDensity,   // initialize to rest density to avoid div-by-zero
                        pressure = 0f,
                    };        
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
        }
    }

}


