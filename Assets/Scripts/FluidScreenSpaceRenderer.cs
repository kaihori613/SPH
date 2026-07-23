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

    RenderTexture _rtThickness, _rtThicknessPing;
    RenderTexture _rtDepthFront;    // front depth as COLOR (view-space z)
    RenderTexture _rtMrtDepth;      // dummy 24-bit depth for MRT binding
    Mesh _quadMesh;
    CommandBuffer _cmd;             // immediate-mode draw into the MRT (see OnRenderImage)
    ComputeBuffer _argsBuf;         // persistent: the render thread reads it after OnRenderImage returns

    [Header("Look")]
    [Range(1.0f, 2.0f)] public float renderRadiusScale = 1.4f;


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

        // Dummy depth RT for MRT binding (same size)
        _rtMrtDepth = new RenderTexture(rw, rh, 24, RenderTextureFormat.Depth)
        { name = "Fluid_MRT_Depth" };
        _rtMrtDepth.Create();
    }

    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (!cam) cam = Camera.main;

        // bail if not set up
        if (!sph || sph._particlesBuffer == null || sph._particlesBuffer.count == 0 || !thicknessMat || !compositeMat)
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
        compositeMat.SetTexture("_DepthTex", _rtDepthFront);
        compositeMat.SetTexture("_ThicknessTex", _rtThickness);
        compositeMat.SetMatrix ("_Proj", cam.projectionMatrix);

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
