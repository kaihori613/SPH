using UnityEngine;

// Switches between the two ways of looking at the sim, at runtime:
//
//   Particles  — instanced spheres coloured by density/speed. Cheap, and the mode you
//                actually inspect the physics in (density colour = incompressibility check).
//   Surface    — screen-space fluid: thickness + front-depth + refraction composite.
//                Looks like water, costs an extra full-screen pass set per frame.
//
// Starts in Particles so entering play mode is always the cheap path. Nothing here touches
// the solver — it only flips render flags.
[DisallowMultipleComponent]
public class FluidViewToggle : MonoBehaviour
{
    public enum ViewMode { Particles, Surface }

    [Header("Refs (auto-found if left empty)")]
    public SPH sph;
    public FluidScreenSpaceRenderer surfaceRenderer;

    [Header("Startup")]
    [Tooltip("Mode entered on Play. Particles is the cheaper one.")]
    public ViewMode startMode = ViewMode.Particles;
    [Tooltip("Force the particle colour mode on Play so the density view is what you get by default.")]
    public bool forceColorModeOnStart = true;
    public SPH.ColorMode startColorMode = SPH.ColorMode.Density;

    [Header("Input")]
    [Tooltip("Left-click anywhere in the Game view to switch modes.")]
    public bool toggleOnClick = true;
    [Tooltip("Also switch on this key. None to disable.")]
    public KeyCode toggleKey = KeyCode.Space;

    [Header("HUD")]
    public bool showLabel = true;

    private ViewMode _mode;
    private GUIStyle _style;

    void Start()
    {
        if (!sph) sph = FindAnyObjectByType<SPH>();
        if (!surfaceRenderer) surfaceRenderer = FindAnyObjectByType<FluidScreenSpaceRenderer>();

        if (forceColorModeOnStart && sph)
        {
            sph.particleColorMode = startColorMode;
            sph.showColor = true;
        }

        Apply(startMode);
    }

    void Update()
    {
        bool clicked = toggleOnClick && Input.GetMouseButtonDown(0);
        bool keyed = toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey);
        if (clicked || keyed)
            Apply(_mode == ViewMode.Particles ? ViewMode.Surface : ViewMode.Particles);
    }

    public void Apply(ViewMode m)
    {
        _mode = m;
        bool surface = m == ViewMode.Surface;

        // Spheres must be off in Surface mode: they render into the scene colour buffer that
        // the composite then refracts, so you'd see balls through the water.
        if (sph) sph.showSpheres = !surface;

        // Disabling the component skips OnRenderImage entirely and frees its render textures,
        // so Particles mode costs nothing extra.
        if (surfaceRenderer) surfaceRenderer.enabled = surface;
    }

    void OnGUI()
    {
        if (!showLabel) return;

        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.UpperLeft };
            _style.normal.textColor = Color.white;
        }

        string how = toggleOnClick ? "click" : toggleKey.ToString();
        string colour = (sph && _mode == ViewMode.Particles)
            ? $" — colour: {sph.particleColorMode}"
            : "";
        GUI.Label(new Rect(10, 10, 600, 22), $"View: {_mode}{colour}   ({how} to switch)", _style);
    }
}
