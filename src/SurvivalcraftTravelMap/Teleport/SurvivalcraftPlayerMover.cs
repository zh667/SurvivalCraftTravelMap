namespace SurvivalcraftTravelMap.Teleport;

using Game;

public interface ISurvivalcraftPlayerFacade
{
    PlayerMovementSnapshot ReadMovement();

    void WriteMovement(PlayerMovementSnapshot movement);
}

public sealed class SurvivalcraftPlayerMover : IPlayerMover
{
    private readonly ISurvivalcraftPlayerFacade _facade;

    public SurvivalcraftPlayerMover(ISurvivalcraftPlayerFacade facade)
    {
        ArgumentNullException.ThrowIfNull(facade);
        _facade = facade;
    }

    public SurvivalcraftPlayerMover(ComponentPlayer player, GameUpdateDispatcher dispatcher)
        : this(new SurvivalcraftPlayerFacade(player, dispatcher))
    {
    }

    public PlayerMovementSnapshot CaptureSnapshot() => _facade.ReadMovement();

    public void Move(PlayerMovementSnapshot movement) => _facade.WriteMovement(movement with
    {
        LinearVelocity = System.Numerics.Vector3.Zero,
        AngularVelocity = System.Numerics.Vector3.Zero,
        FallDistance = 0f,
        IsFalling = false,
        HasPendingFallDamage = false,
    });

    public void Restore(PlayerMovementSnapshot snapshot) => _facade.WriteMovement(snapshot);
}

internal sealed class SurvivalcraftPlayerFacade : ISurvivalcraftPlayerFacade
{
    private readonly ComponentBody _body;
    private readonly GameUpdateDispatcher _dispatcher;

    internal SurvivalcraftPlayerFacade(ComponentPlayer player, GameUpdateDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _body = player.ComponentBody;
        _dispatcher = dispatcher;
    }

    public PlayerMovementSnapshot ReadMovement() => _dispatcher.Invoke(() =>
    {
        var velocity = ToNumerics(_body.Velocity);
        var collisionVelocity = _body.CollisionVelocityChange;
        var isFalling = _body.StandingOnBody is null
            && _body.StandingOnValue is null
            && velocity.Y < 0f;
        return new PlayerMovementSnapshot(
            ToNumerics(_body.Position),
            ToNumerics(_body.Rotation),
            velocity,
            System.Numerics.Vector3.Zero,
            0f,
            isFalling,
            collisionVelocity.Y > 0f);
    });

    public void WriteMovement(PlayerMovementSnapshot movement) => _dispatcher.Invoke(() =>
    {
        _body.Position = ToEngine(movement.Position);
        _body.Rotation = ToEngine(movement.Rotation);
        _body.Velocity = ToEngine(movement.LinearVelocity);
        _body.CollisionVelocityChange = Engine.Vector3.Zero;
        _body.StandingOnBody = null!;
        _body.StandingOnValue = null;
        _body.StandingOnVelocity = Engine.Vector3.Zero;
    });

    private static System.Numerics.Vector3 ToNumerics(Engine.Vector3 value) =>
        new(value.X, value.Y, value.Z);

    private static System.Numerics.Quaternion ToNumerics(Engine.Quaternion value) =>
        new(value.X, value.Y, value.Z, value.W);

    private static Engine.Vector3 ToEngine(System.Numerics.Vector3 value) =>
        new(value.X, value.Y, value.Z);

    private static Engine.Quaternion ToEngine(System.Numerics.Quaternion value) =>
        new(value.X, value.Y, value.Z, value.W);
}
