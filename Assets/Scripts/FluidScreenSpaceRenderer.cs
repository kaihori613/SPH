using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public class FluidScreenSpaceRenderer : MonoBehaviour
{
    [Header("Scene refs")]
    public Camera cam;
    public SPH sph;                    // your SPH component (owns _particlesBuffer / particleRadius)
    
    [Header("Materials")]
    public Material thicknessMat;      // Shader = Hidden/Fluid/ParticlesThickness  (or Fluid/ParticlesThickness)
    public Material blurMat;           // (optional) Shader = Hidden/Fluid/BilateralBlur
    public Material compositeMat;

    [Header("Settings")]
    [Tooltip("Fluid buffers are rendered at this fraction of screen resolution. 1 = full res (sharpest, most expensive).")]
    [Range(0.25f, 1f)]
    public float resolutionScale = 1f;

    [Header("Depth smoothing (Green GDC10)")]
    [Tooltip("Bilateral-smooth the front depth before deriving normals (done in the composite " +
             "shader). Without this the surface shows one bump per particle. Main quality knob.")]
    public bool smoothDepth = true;
    [Range(1, 12)]
    [Tooltip("Kernel half-width in depth-buffer texels. Cost is O((2r+1)^2) taps per pixel, so " +
             "keep it modest — 4-6 is usually enough. Larger = smoother but washes out detail.")]
    public int depthBlurRadius = 5;
    [Range(1, 4)]
    [Tooltip("Subsample the blur kernel every N texels. 1 = full quality (every texel), 2 = " +
             "~4x fewer taps for a wider kernel at the same radius. The per-tap gaussian uses " +
             "the true offset, so the kernel width is preserved; higher strides trade a little " +
             "smoothness for speed. This is the cheap in-pass cost lever until a proper " +
             "separable (2x1D) blur lands.")]
    public int depthBlurStride = 2;
    [Tooltip("Spatial falloff in texels.")]
    public float depthSigmaSpatial = 4f;
    [Tooltip("Depth-difference tolerance in WORLD units. Around a particle radius or two: big " +
             "enough to smooth bumps, small enough that separate surfaces stay separate.")]
    public float depthSigmaRange = 0.4f;

    [Header("Curvature flow (van der Laan 2009)")]
    [Tooltip("Iteratively evolve the front-depth surface along its mean curvature BEFORE the " +
             "composite reconstructs normals. Unlike the bilateral blur, it dissolves the " +
             "high-curvature per-particle bumps while preserving flat pools and gentle waves — " +
             "the surface-reconstruction fix for the 'glass beads' that widening the blur can't " +
             "touch. Ping-pongs two depth RTs via CommandBuffer DrawMesh (the Metal-safe path). " +
             "When on, turn depthBlurRadius down (or off) to see it in isolation.")]
    public bool useCurvatureFlow = false;
    [Range(0, 120)]
    [Tooltip("Number of curvature-flow iterations. van der Laan uses tens; more = smoother but " +
             "costs one fullscreen pass each. With stride 2 and the step clamp, ~15-20 matches " +
             "what 40 dense iterations did.")]
    public int curvatureIterations = 20;
    [Tooltip("Explicit Euler step size. With the per-step clamp this is now forgiving — raise it " +
             "to smooth faster; if the surface inverts, flip its sign. Tune with iterations.")]
    public float curvatureDt = 0.01f;
    [Tooltip("Caps |depth change| per iteration (world units, ~a fraction of particleRadius). " +
             "This is what kills the per-pixel sparkle: it stops the flow overshooting into a " +
             "salt-and-pepper instability. Lower = more stable/slower to converge.")]
    public float curvatureMaxStep = 0.02f;
    [Range(1, 4)]
    [Tooltip("Finite-difference stencil width in texels. 1 (recommended) sees the fine " +
             "per-particle beads and dissolves them; 2+ straddles them (skips both the noise AND " +
             "the beads) so the surface stays granular. Keep at 1 for bead removal; let the " +
             "Max Step clamp + composite plane-fit handle the sparkle instead.")]
    public int curvatureStride = 1;
    [Tooltip("Insurance toggle: if curvature flow makes the water vanish or render upside-down, " +
             "a Metal RT viewport Y-flip is the cause — flip this. Costs nothing.")]
    public bool curvatureFlipY = false;
    [Tooltip("Shader = Fluid/CurvatureFlow. Assign the material in the Inspector.")]
    public Material curvatureFlowMat;

    RenderTexture _rtThickness, _rtThicknessPing;
    RenderTexture _rtDepthFront;    // front depth as COLOR (view-space z)
    RenderTexture _rtDepthPing;     // curvature-flow ping-pong target (same format as front)
    RenderTexture _rtMrtDepth;      // dummy 24-bit depth for MRT binding
    Mesh _quadMesh;
    CommandBuffer _cmd;             // immediate-mode draw into the MRT (see OnRenderImage)
    ComputeBuffer _argsBuf;         // persistent: the render thread reads it after OnRenderImage returns

    [Header("Look")]
    [Range(1.0f, 2.0f)] public float renderRadiusScale = 1.4f;

    [Header("Anisotropic splat (Yu-Turk Stage 2)")]
    [Range(0.15f, 1f)]
    [Tooltip("Ellipsoid thickness along the surface normal. 1 = spheres (Stage 1 look); lower = " +
             "flatter surface-aligned discs that fuse into a smoother surface. ~0.4-0.6 is a good start.")]
    public float flattenK = 0.5f;
    [Tooltip("Neighbour count below which a particle is treated as isolated spray and shrunk out of " +
             "the surface (stops lone droplets rendering as dark-rimmed domes). Surface particles have " +
             "~15-25 neighbours, spray has <8. Raise to cull more aggressively.")]
    public float minSurfaceNbrs = 8f;
    [Range(0f, 1f)]
    [Tooltip("Radius multiplier for a fully-isolated particle. 0 = cull it entirely; ~0.3 = shrink to " +
             "a small speck. Well-connected surface particles always keep full radius.")]
    public float isolatedScale = 0.15f;


    void OnEnable()
    {
        if (!cam) cam = GetComponent<Camera>();
        if (cam) cam.depthTextureMode |= DepthTextureMode.Depth; // for occlusion in Composite
        if (!_quadMesh) _quadMesh = MakeUnitQuad();
        if (thicknessMat) thicknessMat.enableInstancing = true; // required for InstancedIndirect
    }

    void OnDisable()
    {
        ReleaseRTs();
        _cmd?.Release();
        _cmd = null;
        _argsBuf?.Release();
        _argsBuf = null;
    }

    void ReleaseRTs()
    {
        if (_rtThickness)     { _rtThickness.Release();     _rtThickness = null; }
        if (_rtThicknessPing) { _rtThicknessPing.Release(); _rtThicknessPing = null; }
        if (_rtDepthFront)    { _rtDepthFront.Release();    _rtDepthFront = null; }
        if (_rtDepthPing)     { _rtDepthPing.Release();     _rtDepthPing = null; }
        if (_rtMrtDepth)      { _rtMrtDepth.Release();      _rtMrtDepth = null; }
    }

    void EnsureRTs(int fullW, int fullH)
    {
        int rw = Mathf.Max(1, Mathf.RoundToInt(fullW * resolutionScale));
        int rh = Mathf.Max(1, Mathf.RoundToInt(fullH * resolutionScale));

        bool need = _rtThickness == null || _rtThickness.width != rw || _rtThickness.height != rh;
        if (!need) return;

        ReleaseRTs();

        _rtThickness = new RenderTexture(rw, rh, 0, RenderTextureFormat.RHalf)
        { name = "Fluid_Thickness", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        _rtThickness.Create();

        _rtThicknessPing = new RenderTexture(rw, rh, 0, RenderTextureFormat.RHalf)
        { name = "Fluid_Thickness_Ping", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        _rtThicknessPing.Create();

        // FRONT DEPTH as COLOR RT (no depth buffer here)
        _rtDepthFront = new RenderTexture(rw, rh, 0, RenderTextureFormat.RFloat)
        { name = "Fluid_FrontDepth", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        _rtDepthFront.Create();

        // Curvature-flow ping-pong partner (same format/size). Point filtering: the flow reads
        // exact texels; bilinear would blur the sentinel edge into the fluid.
        _rtDepthPing = new RenderTexture(rw, rh, 0, RenderTextureFormat.RFloat)
        { name = "Fluid_FrontDepth_Ping", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        _rtDepthPing.Create();

        // Dummy depth RT for MRT binding (same size)
        _rtMrtDepth = new RenderTexture(rw, rh, 24, RenderTextureFormat.Depth)
        { name = "Fluid_MRT_Depth" };
        _rtMrtDepth.Create();
    }

    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (!cam) cam = Camera.main;

        // bail if not set up
        if (!sph || sph._particlesBuffer == null || sph._particlesBuffer.count == 0 || sph.RenderPositions == null || sph.RenderAniso == null || !thicknessMat || !compositeMat)
        {
            Graphics.Blit(src, dst);
            return;
        }

        EnsureRTs(src.width, src.height);

        // 1. Thickness + FrontDepth (MRT, half-res)
        // GPU expects the API projection matrix (Metal/D3D convention + render-into-RT flip),
        // not the raw GL-convention cam.projectionMatrix.
        var proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, renderIntoTexture: true);
        var vp = proj * cam.worldToCameraMatrix;
        thicknessMat.SetMatrix("_VP", vp);
        // Passed explicitly: the CommandBuffer draw below runs outside the camera's render
        // loop, so built-ins like UNITY_MATRIX_V are not set up for this camera.
        thicknessMat.SetMatrix("_View", cam.worldToCameraMatrix);
        thicknessMat.SetVector("_CamRight", cam.transform.right);
        thicknessMat.SetVector("_CamUp",    cam.transform.up);
        thicknessMat.SetFloat ("_ParticleRadius", sph.particleRadius * renderRadiusScale);
        thicknessMat.SetBuffer("_particlesBuffer", sph._particlesBuffer);
        thicknessMat.SetBuffer("_renderPositions", sph.RenderPositions); // Yu-Turk smoothed centres (Stage 1)
        thicknessMat.SetBuffer("_renderAniso", sph.RenderAniso);          // Stage 2 flatten axis + confidence
        thicknessMat.SetFloat("_FlattenK", flattenK);
        thicknessMat.SetFloat("_MinSurfaceNbrs", minSurfaceNbrs);
        thicknessMat.SetFloat("_IsolatedScale", isolatedScale);

         // Clear thickness to 0
        Graphics.SetRenderTarget(_rtThickness);
        GL.Clear(false, true, Color.clear);

        // Front depth uses BlendOp Max → clear to -INF so any real value (e.g. -3) wins
        Graphics.SetRenderTarget(_rtDepthFront);
        GL.Clear(false, true, new Color(-1e20f, 0, 0, 0));

        if (!_quadMesh) _quadMesh = MakeUnitQuad();

        uint[] args = {
            (uint)_quadMesh.GetIndexCount(0),
            (uint)sph._particlesBuffer.count,
            (uint)_quadMesh.GetIndexStart(0),
            (uint)_quadMesh.GetBaseVertex(0),
            0
        };

        // Persistent args buffer: disposing it in the same frame (the old using-block) is a
        // race — the render thread reads the indirect args after this method returns.
        if (_argsBuf == null) _argsBuf = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
        _argsBuf.SetData(args);

        // Graphics.DrawMeshInstancedIndirect only ENQUEUES the mesh for the camera's next
        // render loop — inside OnRenderImage it never hits the RT bound here and the
        // thickness/front-depth targets stay empty. A CommandBuffer executed immediately
        // is the synchronous path.
        // Two single-RT draws (not MRT): per-render-target blend state is silently ignored
        // on Metal here, which broke thickness accumulation. Shader pass 0 = additive
        // thickness, pass 1 = Max front depth.
        if (_cmd == null) _cmd = new CommandBuffer { name = "FluidThickness" };
        _cmd.Clear();
        _cmd.SetRenderTarget(_rtThickness);
        _cmd.DrawMeshInstancedIndirect(_quadMesh, 0, thicknessMat, 0, _argsBuf);
        _cmd.SetRenderTarget(_rtDepthFront);
        _cmd.DrawMeshInstancedIndirect(_quadMesh, 0, thicknessMat, 1, _argsBuf);

        // 1b. Curvature flow: N mean-curvature-flow iterations on the front depth, appended to
        // the SAME command buffer (ordered after the splat draws). Ping-pong Front<->Ping using
        // SetRenderTarget + a fullscreen DrawMesh — NOT Blit, which collapses the RFloat RT to
        // background on this Metal path. _DepthTex is rebound (global) each iteration to the read
        // side; Unity keeps _DepthTex_TexelSize in sync with it.
        RenderTexture depthForComposite = _rtDepthFront;
        if (useCurvatureFlow && curvatureFlowMat && curvatureIterations > 0)
        {
            curvatureFlowMat.SetMatrix("_Proj", cam.projectionMatrix);
            curvatureFlowMat.SetFloat ("_FlowDt", curvatureDt);
            curvatureFlowMat.SetFloat ("_MaxStep", curvatureMaxStep);
            curvatureFlowMat.SetInt   ("_FlowStride", Mathf.Max(1, curvatureStride));
            curvatureFlowMat.SetFloat ("_UvFlipY", curvatureFlipY ? 1f : 0f);

            // Both ping RTs share this size; bind explicitly so the flow shader's texel step is
            // correct even if the auto _TexelSize companion isn't populated for a cmd-global set.
            int dw = _rtDepthFront.width, dh = _rtDepthFront.height;
            _cmd.SetGlobalVector("_DepthTex_TexelSize", new Vector4(1f / dw, 1f / dh, dw, dh));

            RenderTexture read = _rtDepthFront, write = _rtDepthPing;
            for (int it = 0; it < curvatureIterations; it++)
            {
                _cmd.SetGlobalTexture("_DepthTex", read);
                _cmd.SetRenderTarget(write);
                _cmd.DrawMesh(_quadMesh, Matrix4x4.identity, curvatureFlowMat, 0, 0);
                var tmp = read; read = write; write = tmp;   // swap
            }
            depthForComposite = read;   // last buffer written is now the read side after the final swap
        }

        Graphics.ExecuteCommandBuffer(_cmd);

        // 2. Depth smoothing is done INSIDE the composite shader (bilateral tap before normal
        // reconstruction), not as a separate blur pass. The separable ping-pong approach kept
        // collapsing the front-depth RT to background on Metal inside OnRenderImage; folding it
        // into the composite needs no extra render targets and can't hit that ordering bug.
        compositeMat.SetInt  ("_SmoothRadius", smoothDepth ? depthBlurRadius : 0);
        compositeMat.SetInt  ("_SmoothStride", Mathf.Max(1, depthBlurStride));
        compositeMat.SetFloat("_SmoothSigmaS", depthSigmaSpatial);
        compositeMat.SetFloat("_SmoothSigmaR", depthSigmaRange);

        // 4. Composite Pass
        // Sun for the specular term, in view space (the composite works in view space).
        var sun = RenderSettings.sun;
        if (sun)
        {
            Vector3 sunDirVS = cam.worldToCameraMatrix.MultiplyVector(-sun.transform.forward);
            compositeMat.SetVector("_SunDirVS", sunDirVS.normalized);
            compositeMat.SetColor("_SunColor", sun.color * sun.intensity);
        }
        else
        {
            compositeMat.SetColor("_SunColor", Color.black); // no sun, no specular
        }
        compositeMat.SetTexture("_SceneTex", src);
        compositeMat.SetTexture("_DepthTex", depthForComposite); // curvature-smoothed when enabled
        compositeMat.SetTexture("_ThicknessTex", _rtThickness);
        compositeMat.SetMatrix ("_Proj", cam.projectionMatrix);
        // Passed explicitly rather than relying on unity_CameraToWorld: the composite runs as a
        // Blit inside OnRenderImage, and this file already avoids trusting camera built-ins here.
        // Used to take the view-space reflection dir into world space for the env lookup.
        compositeMat.SetMatrix ("_CamToWorld", cam.cameraToWorldMatrix);

        Graphics.Blit(src, dst, compositeMat);
    }

    Mesh MakeUnitQuad()
    {
        var m = new Mesh();
        m.vertices = new[] {
            new Vector3(-1,-1,0),
            new Vector3(1,-1,0),
            new Vector3(1,1,0),
            new Vector3(-1,1,0)
        };
        
        m.uv = new[] {
            new Vector2(0,0),
            new Vector2(1,0),
            new Vector2(1,1),
            new Vector2(0,1)
        };

        m.triangles = new[] { 0,1,2, 0,2,3 };
        m.RecalculateBounds();
        m.UploadMeshData(true);

        return m;
    }
}
