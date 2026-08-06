using UnityEngine;

// Drops an object under gravity and floats it in the SPH fluid via a simple Archimedes
// buoyancy model against the still-water surface. Self-integrated (no Rigidbody), so it can
// never tunnel through the GPU-fluid box the way a Unity rigidbody with no floor collider would.
//
// The fluid already reacts to this object one-way: SPH reads collisionSphere.position every step
// and pushes particles out of the sphere, so as the ball drops it splashes. This script supplies
// the missing other half (fluid -> ball) as an analytic buoyancy + drag force, which together
// reads as a convincing drop-splash-bob-float.
//
// Setup: put this on the white ball, assign `sph` (for auto water level + box clamping), position
// the ball ABOVE the water, press Play. Tune `relativeDensity` for how high it floats.
public class BuoyantBall : MonoBehaviour
{
    [Tooltip("The SPH sim. Used to auto-estimate the water surface and to clamp the ball inside the " +
             "fluid box. Optional — if unset, set Water Level Y manually and box clamping is skipped.")]
    public SPH sph;

    [Tooltip("Ball radius in world units. Auto-filled from the object's scale on Reset; override if wrong.")]
    public float radius = 0.5f;

    [Tooltip("Ball density relative to water (1000 kg/m^3). < 1 floats, > 1 sinks. " +
             "0.5 floats roughly half-submerged; 0.3 rides high; 0.9 barely bobs.")]
    [Range(0.05f, 3f)]
    public float relativeDensity = 0.5f;

    [Tooltip("Auto-estimate the still-water surface height from the SPH fill (particle count vs box). " +
             "Uncheck to use Water Level Y directly.")]
    public bool autoWaterLevel = true;

    [Tooltip("World Y of the still-water surface. Auto-filled when Auto Water Level is on; otherwise " +
             "eyeball it in Play until the ball floats at the surface.")]
    public float waterLevelY = 0f;

    [Tooltip("On Play, lift the ball to (water surface + Drop From Height) so you always see it drop " +
             "and splash. Turn off to keep the ball wherever you placed it.")]
    public bool dropOnPlay = true;
    [Tooltip("How far above the water surface to start the drop when Drop On Play is on.")]
    public float dropFromHeight = 5f;

    [Tooltip("Downward gravity (m/s^2). Match the sim (~9.81).")]
    public float gravity = 9.81f;

    [Tooltip("Linear drag while fully submerged (per second) — damps the bob so it settles.")]
    public float waterDrag = 2.0f;
    [Tooltip("Linear drag in air (per second) — keep small.")]
    public float airDrag = 0.05f;

    [Header("Two-way Coupling (fluid -> ball)")]
    [Tooltip("Take force/torque from the actual fluid particles (needs Enable Ball Coupling on the SPH). " +
             "This is what lets the ball move sideways, spin, and ride waves. Off = pure vertical bobbing.")]
    public bool useFluidCoupling = true;
    [Tooltip("How fast the ball follows the measured wave height. Low = smooth and laggy, " +
             "high = jittery (the probe is a single topmost particle, so it needs some smoothing).")]
    [Range(0.01f, 1f)] public float surfaceFollow = 0.15f;
    [Tooltip("Angular damping (per second) so the spin settles instead of winding up forever.")]
    public float angularDrag = 1.5f;
    [Tooltip("Safety clamp on fluid-driven acceleration (m/s^2). Stops a bad frame launching the ball.")]
    public float maxFluidAccel = 60f;
    [Tooltip("Safety clamp on fluid-driven angular acceleration (rad/s^2).")]
    public float maxAngularAccel = 40f;
    [Tooltip("Restitution when the ball hits a box wall. 0 = stop dead, 1 = perfect bounce.")]
    [Range(0f, 1f)] public float wallBounce = 0.3f;

    const float RhoWater = 1000f;
    Vector3 _vel;
    Vector3 _angVel;          // rad/s, world axes
    float _surfaceEMA = float.NaN;   // smoothed measured wave height under the ball

    void Reset()
    {
        radius = 0.5f * Mathf.Abs(transform.lossyScale.x);
        sph = FindObjectOfType<SPH>();
    }

    void Start()
    {
        // Auto-wire the sim if the Inspector reference is empty — otherwise autoWaterLevel can't
        // run and the ball floats at y=0 (in mid-air above the real surface), the #1 "not floating".
        if (!sph) sph = FindObjectOfType<SPH>();
        if (radius <= 0f) radius = 0.5f * Mathf.Abs(transform.lossyScale.x);

        if (autoWaterLevel && sph) waterLevelY = EstimateWaterLevel();

        if (dropOnPlay)
        {
            Vector3 p = transform.position;
            p.y = waterLevelY + dropFromHeight;   // start above the surface so the drop is visible
            transform.position = p;
            _vel = Vector3.zero;
        }

        Debug.Log($"[BuoyantBall] waterLevelY={waterLevelY:F2}, radius={radius:F2}, " +
                  $"start Y={transform.position.y:F2}, sph={(sph ? sph.name : "NULL")}");
    }

    // Rest water surface = box floor + (fluid volume / box cross-section). The sim's box is centred
    // on the world origin (Integrate clamps to +/- boxSize/2), so the floor is -boxSize.y/2.
    float EstimateWaterLevel()
    {
        Vector3 box = sph.boxSize;
        float spacing = sph.particleRadius * 2f;                 // packed spacing
        long n = (long)sph.numToSpawn.x * sph.numToSpawn.y * sph.numToSpawn.z;
        float fluidVol = n * spacing * spacing * spacing;
        float fillHeight = fluidVol / Mathf.Max(box.x * box.z, 1e-3f);
        float floorY = -box.y * 0.5f + sph.particleRadius;
        return floorY + fillHeight;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        Vector3 pos = transform.position;
        float r = Mathf.Max(radius, 1e-3f);

        float vSphere = (4f / 3f) * Mathf.PI * r * r * r;
        float mass = relativeDensity * RhoWater * vSphere;       // ball mass from relative density

        // Hand our velocity to the sim so the GPU can compute relative-velocity drag this step.
        if (sph) sph.SetBallVelocity(_vel);

        // Pull the fluid's reaction (force, torque) and the MEASURED local wave height. Without this
        // the surface is a fixed plane and every force term is ±Y, so the ball can only bob.
        Vector3 fFluid = Vector3.zero, tFluid = Vector3.zero;
        bool coupled = false;
        if (useFluidCoupling && sph &&
            sph.TryGetBallCoupling(out fFluid, out tFluid, out float surfY, out int samples))
        {
            coupled = samples > 0;
            // The probe is the single topmost particle in the ball's column, so it jitters by a
            // particle diameter frame to frame — smooth it into a surface the ball can ride.
            if (!float.IsNaN(surfY))
                _surfaceEMA = float.IsNaN(_surfaceEMA) ? surfY : Mathf.Lerp(_surfaceEMA, surfY, surfaceFollow);
        }

        // Ride the real waves when we have a reading; fall back to the still-water estimate otherwise
        // (ball in mid-air on the way down, or coupling disabled).
        float level = (coupled && !float.IsNaN(_surfaceEMA)) ? _surfaceEMA : waterLevelY;

        // Submerged spherical-cap volume from how far the ball's lowest point sits below the surface.
        float h = Mathf.Clamp(level - (pos.y - r), 0f, 2f * r);       // submersion depth [0, diameter]
        float vSub = Mathf.PI * h * h * (3f * r - h) / 3f;            // cap volume
        float submergedFrac = vSub / vSphere;

        // Accelerations: weight down, buoyancy up (rho_water * g * V_submerged / m), velocity drag.
        Vector3 a = Vector3.down * gravity;
        a += Vector3.up * (RhoWater * gravity * vSub) / Mathf.Max(mass, 1e-6f);
        a += -_vel * Mathf.Lerp(airDrag, waterDrag, submergedFrac);

        // The fluid's own push — a full 3-vector, so a sideways slosh finally moves the ball sideways.
        if (coupled)
        {
            Vector3 aFluid = fFluid / Mathf.Max(mass, 1e-6f);
            if (aFluid.magnitude > maxFluidAccel) aFluid = aFluid.normalized * maxFluidAccel;
            a += aFluid;
        }

        _vel += a * dt;
        pos += _vel * dt;

        // Rotation: torque from off-centre fluid pushes, against a solid sphere's inertia (2/5 m r^2).
        if (coupled)
        {
            float inertia = 0.4f * mass * r * r;
            Vector3 angAcc = tFluid / Mathf.Max(inertia, 1e-6f);
            if (angAcc.magnitude > maxAngularAccel) angAcc = angAcc.normalized * maxAngularAccel;
            _angVel += angAcc * dt;
        }
        _angVel *= Mathf.Exp(-angularDrag * Mathf.Lerp(0.2f, 1f, submergedFrac) * dt);

        float spin = _angVel.magnitude;
        if (spin > 1e-5f)
            transform.rotation = Quaternion.AngleAxis(spin * Mathf.Rad2Deg * dt, _angVel / spin) * transform.rotation;

        // Keep the ball inside the fluid box (walls + floor) so it can't drift out or tunnel through.
        if (sph)
        {
            Vector3 half = sph.boxSize * 0.5f;
            float lo, hi;
            // Bounce off the walls rather than dead-stopping. With lateral motion now possible, a
            // hard zero read as the ball sticking to the glass.
            lo = -half.x + r; hi = half.x - r;
            if (pos.x < lo) { pos.x = lo; _vel.x = Mathf.Abs(_vel.x) * wallBounce; }
            else if (pos.x > hi) { pos.x = hi; _vel.x = -Mathf.Abs(_vel.x) * wallBounce; }
            lo = -half.z + r; hi = half.z - r;
            if (pos.z < lo) { pos.z = lo; _vel.z = Mathf.Abs(_vel.z) * wallBounce; }
            else if (pos.z > hi) { pos.z = hi; _vel.z = -Mathf.Abs(_vel.z) * wallBounce; }
            float floorY = -half.y + r;
            if (pos.y < floorY) { pos.y = floorY; if (_vel.y < 0) _vel.y = 0; }
        }

        transform.position = pos;
    }
}
