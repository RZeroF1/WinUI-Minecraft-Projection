using System.Numerics;

namespace RZeroF1.Minecraft.Projection;

public enum ProjectionCameraMode
{
    Orbit,
    Free
}

public sealed class ProjectionCamera
{
    private float _yaw = MathF.PI * .25f;
    private float _pitch = MathF.PI * .25f;
    private float _freeYaw;
    private float _freePitch;
    private float _distance = 12;

    public Vector3 OrbitTarget { get; private set; }
    public Vector3 ModelCenter { get; private set; }
    public Vector3 FreePosition { get; private set; }
    public ProjectionCameraMode Mode { get; set; }
    public float SceneScale { get; private set; } = 1;
    public float NearDistance { get; private set; } = .08f;
    public float FarDistance { get; private set; } = 4096;
    public float NormalMoveSpeed { get; private set; } = 2;
    public float Distance => _distance;

    public void Initialize(ProjectionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        SceneScale = MathF.Max(1f, MathF.Sqrt(
            document.Width * (float)document.Width +
            document.Height * (float)document.Height +
            document.Length * (float)document.Length));
        NearDistance = MathF.Max(.08f, SceneScale * .0005f);
        FarDistance = MathF.Max(4096f, SceneScale * 16f);
        NormalMoveSpeed = MathF.Max(.75f, SceneScale * .12f);
        ModelCenter = DocumentCenter(document);
        OrbitTarget = ModelCenter;
        _distance = Math.Clamp(SceneScale * 1.65f, NearDistance, FarDistance);
        _yaw = MathF.PI * .25f;
        _pitch = MathF.PI * .25f;
        FreePosition = CurrentOrbitEye;
        SetFreeDirection(Vector3.Normalize(OrbitTarget - FreePosition));
    }

    public Vector3 Eye => Mode == ProjectionCameraMode.Free
        ? FreePosition
        : CurrentOrbitEye;

    public Vector3 Target => Mode == ProjectionCameraMode.Free
        ? FreePosition + FreeForward
        : OrbitTarget;

    public Vector3 FreeForward
    {
        get
        {
            var cosPitch = MathF.Cos(_freePitch);
            return Vector3.Normalize(new Vector3(
                MathF.Cos(_freeYaw) * cosPitch,
                MathF.Sin(_freePitch),
                MathF.Sin(_freeYaw) * cosPitch));
        }
    }

    public Vector3 FreeRight => Vector3.Normalize(Vector3.Cross(Vector3.UnitY, FreeForward));

    public Vector3 OrbitForward => Vector3.Normalize(OrbitTarget - CurrentOrbitEye);

    public void LookDirection(Vector3 direction)
    {
        if (Mode == ProjectionCameraMode.Free)
            SetFreeDirection(direction);
        else
        {
            if (direction.LengthSquared() < .0001f)
                direction = Vector3.UnitZ;
            direction = Vector3.Normalize(direction);
            var radial = -direction;
            _yaw = MathF.Atan2(radial.Z, radial.X);
            _pitch = MathF.Asin(Math.Clamp(radial.Y, -1f, 1f));
        }
    }

    public void Rotate(float deltaX, float deltaY)
    {
        if (Mode == ProjectionCameraMode.Free)
        {
            _freeYaw -= deltaX * .008f;
            _freePitch = Math.Clamp(_freePitch - deltaY * .008f, -1.54f, 1.54f);
        }
        else
        {
            _yaw -= deltaX * .008f;
            _pitch = Math.Clamp(_pitch - deltaY * .008f, -1.45f, 1.45f);
        }
    }

    public void Pan(float deltaX, float deltaY, float viewportHeight)
    {
        var forward = Mode == ProjectionCameraMode.Free
            ? FreeForward
            : Vector3.Normalize(OrbitTarget - CurrentOrbitEye);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var up = Vector3.Normalize(Vector3.Cross(forward, right));
        var amount = Math.Max(.01f, _distance * 1.5f / Math.Max(1f, viewportHeight));
        if (Mode == ProjectionCameraMode.Free)
            FreePosition += (-right * deltaX + up * deltaY) * amount;
        else
            OrbitTarget += (-right * deltaX + up * deltaY) * amount;
    }

    public void Zoom(float wheelNotches)
    {
        if (Mode == ProjectionCameraMode.Free)
            FreePosition += FreeForward * (NormalMoveSpeed * .35f * wheelNotches);
        else
            _distance = Math.Clamp(_distance * MathF.Pow(.9f, wheelNotches), NearDistance, FarDistance);
    }

    public void MoveFree(Vector3 direction, float deltaSeconds, float speedScale = 1, bool fast = false, bool precise = false)
    {
        if (Mode != ProjectionCameraMode.Free || direction.LengthSquared() == 0)
            return;
        var speed = NormalMoveSpeed * speedScale * (fast ? 4f : 1f) * (precise ? .25f : 1f);
        FreePosition += Vector3.Normalize(direction) * speed * deltaSeconds;
    }

    public void Focus()
    {
        var target = ModelCenter;
        _distance = Math.Clamp(SceneScale * 1.65f, NearDistance, FarDistance);
        if (Mode == ProjectionCameraMode.Free)
        {
            var direction = Vector3.Normalize(target - FreePosition);
            if (direction.LengthSquared() < .0001f)
                direction = Vector3.UnitZ;
            SetFreeDirection(direction);
            FreePosition = target - direction * _distance;
        }
        else OrbitTarget = target;
    }

    public void Reset(ProjectionDocument document)
    {
        var mode = Mode;
        Initialize(document);
        Mode = mode;
    }

    public void SetMode(ProjectionCameraMode mode)
    {
        if (Mode == mode)
            return;
        if (mode == ProjectionCameraMode.Free)
        {
            FreePosition = CurrentOrbitEye;
            SetFreeDirection(Vector3.Normalize(OrbitTarget - FreePosition));
        }
        else
        {
            var forward = FreeForward;
            _distance = Math.Clamp(_distance, NearDistance, FarDistance);
            OrbitTarget = FreePosition + forward * _distance;
            var radial = -forward;
            _yaw = MathF.Atan2(radial.Z, radial.X);
            _pitch = MathF.Asin(Math.Clamp(radial.Y, -1f, 1f));
        }
        Mode = mode;
    }

    private Vector3 CurrentOrbitEye
    {
        get
        {
            var cosPitch = MathF.Cos(_pitch);
            return OrbitTarget + new Vector3(
                MathF.Cos(_yaw) * cosPitch,
                MathF.Sin(_pitch),
                MathF.Sin(_yaw) * cosPitch) * _distance;
        }
    }

    private void SetFreeDirection(Vector3 direction)
    {
        if (direction.LengthSquared() < .0001f)
            direction = Vector3.UnitZ;
        direction = Vector3.Normalize(direction);
        _freeYaw = MathF.Atan2(direction.Z, direction.X);
        _freePitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
    }

    private static Vector3 DocumentCenter(ProjectionDocument document) => new(
        (document.Min.X + document.Max.X + 1) / 2f,
        (document.Min.Y + document.Max.Y + 1) / 2f,
        (document.Min.Z + document.Max.Z + 1) / 2f);
}
