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
    [Range(0.25f, 1f)]
    public float resolutionScale = 0.5f;

    RenderTexture _rtThickness, _rtThicknessPing;
    RenderTexture _rtDepthFront;    // front depth as COLOR (view-space z)
    RenderTexture _rtMrtDepth;      // dummy 24-bit depth for MRT binding
    Mesh _quadMesh;

    [Header("Look")]
    [Range(1.0f, 2.0f)] public float renderRadiusScale = 1.4f;


    void OnEnable()
    {
        if (!cam) cam = GetComponent<Camera>();
        if (cam) cam.depthTextureMode |= DepthTextureMode.Depth; // for occlusion in Composite
        if (!_quadMesh) _quadMesh = MakeUnitQuad();
        if (thicknessMat) thicknessMat.enableInstancing = true; // required for InstancedIndirect
    }

    void OnDisable() => ReleaseRTs();

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
        var vp = cam.projectionMatrix * cam.worldToCameraMatrix;
        thicknessMat.SetMatrix("_VP", vp);
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

        // Bind MRT (thickness, front-depth) with a valid depth buffer
        var mrt = new RenderBuffer[] { _rtThickness.colorBuffer, _rtDepthFront.colorBuffer };
        Graphics.SetRenderTarget(mrt, _rtMrtDepth.depthBuffer);

        if (!_quadMesh) _quadMesh = MakeUnitQuad();

        uint[] args = {
            (uint)_quadMesh.GetIndexCount(0),
            (uint)sph._particlesBuffer.count,
            (uint)_quadMesh.GetIndexStart(0),
            (uint)_quadMesh.GetBaseVertex(0),
            0
        };
        
        using (var argsBuf = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments))
        {
            argsBuf.SetData(args);

            //var center = sph.transform.TransformPoint(sph.spawnCenter); // center on your fluid volume
            var center = sph.spawnCenter; 
            var size = sph.boxSize * 2f;                               // generous bounds to avoid culling
            var bounds = new Bounds(center, size);

            int drawLayer = sph.gameObject.layer;

            Graphics.DrawMeshInstancedIndirect(_quadMesh, 0, thicknessMat, bounds, argsBuf,
            0, null, ShadowCastingMode.Off, receiveShadows:false, layer:drawLayer, camera:cam);
        }

        // 2. Blur Pass (Bilateral Filter)
        if (blurMat)
        {
            blurMat.SetTexture("_Guide", _rtDepthFront);
            Graphics.Blit(_rtThickness, _rtThicknessPing, blurMat, 0);
            blurMat.SetTexture("_Guide", _rtDepthFront);
            Graphics.Blit(_rtThicknessPing, _rtThickness, blurMat, 1);
        }

        // 4. Composite Pass
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
