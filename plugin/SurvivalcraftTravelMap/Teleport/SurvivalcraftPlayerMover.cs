namespace SurvivalcraftTravelMap.Teleport;

using System.Reflection;
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
        NativeState = null,
    });

    public void Restore(PlayerMovementSnapshot snapshot) => _facade.WriteMovement(snapshot);

    public void RestoreSafely(PlayerMovementSnapshot snapshot) => _facade.WriteMovement(snapshot with
    {
        LinearVelocity = System.Numerics.Vector3.Zero,
        AngularVelocity = System.Numerics.Vector3.Zero,
        FallDistance = 0f,
        IsFalling = false,
        HasPendingFallDamage = false,
        NativeState = null,
    });
}

internal sealed class GameUpdateTeleportPositionCommitter(
    GameUpdateDispatcher dispatcher,
    Action synchronizePosition) : ITeleportPositionCommitter
{
    private readonly GameUpdateDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly Action _synchronizePosition =
        synchronizePosition ?? throw new ArgumentNullException(nameof(synchronizePosition));

    public void Commit(Func<bool> commitGuard)
    {
        ArgumentNullException.ThrowIfNull(commitGuard);
        _dispatcher.Invoke(() =>
        {
            if (!commitGuard())
            {
                throw new OperationCanceledException(
                    "The network peer binding changed before authoritative position commit.");
            }

            _synchronizePosition();
        });
    }
}

public readonly record struct SurvivalcraftEngineMovementState(
    System.Numerics.Vector3 Position,
    System.Numerics.Quaternion Rotation,
    System.Numerics.Vector3 LinearVelocity,
    System.Numerics.Vector3 CollisionVelocityChange,
    object? StandingBody,
    int? StandingValue,
    System.Numerics.Vector3 StandingVelocity,
    bool IsFalling,
    bool WasStanding);

public sealed record SurvivalcraftNativeMovementState(
    System.Numerics.Vector3 CollisionVelocityChange,
    object? StandingBody,
    int? StandingValue,
    System.Numerics.Vector3 StandingVelocity,
    bool IsFalling,
    bool WasStanding);

public static class SurvivalcraftMovementStateCodec
{
    public static PlayerMovementSnapshot Capture(SurvivalcraftEngineMovementState state) => new(
        state.Position,
        state.Rotation,
        state.LinearVelocity,
        System.Numerics.Vector3.Zero,
        0f,
        state.IsFalling,
        state.CollisionVelocityChange.Y > 0f && !state.WasStanding,
        new SurvivalcraftNativeMovementState(
            state.CollisionVelocityChange,
            state.StandingBody,
            state.StandingValue,
            state.StandingVelocity,
            state.IsFalling,
            state.WasStanding));

    public static SurvivalcraftEngineMovementState RestoreExact(PlayerMovementSnapshot snapshot)
    {
        if (snapshot.NativeState is not SurvivalcraftNativeMovementState nativeState)
        {
            throw new InvalidOperationException("An exact Survivalcraft restore requires its captured native movement state.");
        }

        return new SurvivalcraftEngineMovementState(
            snapshot.Position,
            snapshot.Rotation,
            snapshot.LinearVelocity,
            nativeState.CollisionVelocityChange,
            nativeState.StandingBody,
            nativeState.StandingValue,
            nativeState.StandingVelocity,
            nativeState.IsFalling,
            nativeState.WasStanding);
    }

    public static SurvivalcraftEngineMovementState RestoreSafely(PlayerMovementSnapshot snapshot) => new(
        snapshot.Position,
        snapshot.Rotation,
        System.Numerics.Vector3.Zero,
        System.Numerics.Vector3.Zero,
        null,
        null,
        System.Numerics.Vector3.Zero,
        false,
        true);
}

internal sealed class SurvivalcraftPlayerFacade : ISurvivalcraftPlayerFacade
{
    private static readonly FieldInfo FallingField = GetRequiredBooleanInstanceField(
        typeof(ComponentLocomotion),
        "m_falling");
    private static readonly FieldInfo WasStandingField = GetRequiredBooleanInstanceField(
        typeof(ComponentHealth),
        "m_wasStanding");

    private readonly ComponentBody _body;
    private readonly ComponentHealth _health;
    private readonly ComponentLocomotion _locomotion;
    private readonly GameUpdateDispatcher _dispatcher;

    internal SurvivalcraftPlayerFacade(ComponentPlayer player, GameUpdateDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _body = player.ComponentBody
            ?? throw new InvalidOperationException("The travel-map player does not have a body component.");
        _health = player.ComponentHealth
            ?? throw new InvalidOperationException("The travel-map player does not have a health component.");
        _locomotion = player.ComponentLocomotion
            ?? throw new InvalidOperationException("The travel-map player does not have a locomotion component.");
        _dispatcher = dispatcher;
    }

    public PlayerMovementSnapshot ReadMovement() => _dispatcher.Invoke(() =>
    {
        var state = new SurvivalcraftEngineMovementState(
            ToNumerics(_body.Position),
            ToNumerics(_body.Rotation),
            ToNumerics(_body.Velocity),
            ToNumerics(_body.CollisionVelocityChange),
            _body.StandingOnBody,
            _body.StandingOnValue,
            ToNumerics(_body.StandingOnVelocity),
            (bool)FallingField.GetValue(_locomotion)!,
            (bool)WasStandingField.GetValue(_health)!);
        return SurvivalcraftMovementStateCodec.Capture(state);
    });

    public void WriteMovement(PlayerMovementSnapshot movement) => _dispatcher.Invoke(() =>
    {
        var state = movement.NativeState is null
            ? SurvivalcraftMovementStateCodec.RestoreSafely(movement)
            : SurvivalcraftMovementStateCodec.RestoreExact(movement);
        _body.Position = ToEngine(state.Position);
        _body.Rotation = ToEngine(state.Rotation);
        _body.Velocity = ToEngine(state.LinearVelocity);
        _body.CollisionVelocityChange = ToEngine(state.CollisionVelocityChange);
        _body.StandingOnBody = state.StandingBody switch
        {
            null => null!,
            ComponentBody body => body,
            _ => throw new InvalidOperationException("Captured standing body is not a Survivalcraft ComponentBody."),
        };
        _body.StandingOnValue = state.StandingValue;
        _body.StandingOnVelocity = ToEngine(state.StandingVelocity);
        FallingField.SetValue(_locomotion, state.IsFalling);
        WasStandingField.SetValue(_health, state.WasStanding);
    });

    internal static FieldInfo GetRequiredBooleanInstanceField(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic;
        var field = type.GetField(name, flags)
            ?? type.GetField(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.FullName, name);
        if (field.IsStatic || field.FieldType != typeof(bool))
        {
            throw new InvalidOperationException(
                $"Field {type.FullName}.{name} must be an instance Boolean field; " +
                $"actual type={field.FieldType.FullName}, static={field.IsStatic}.");
        }

        return field;
    }

    private static System.Numerics.Vector3 ToNumerics(Engine.Vector3 value) =>
        new(value.X, value.Y, value.Z);

    private static System.Numerics.Quaternion ToNumerics(Engine.Quaternion value) =>
        new(value.X, value.Y, value.Z, value.W);

    private static Engine.Vector3 ToEngine(System.Numerics.Vector3 value) =>
        new(value.X, value.Y, value.Z);

    private static Engine.Quaternion ToEngine(System.Numerics.Quaternion value) =>
        new(value.X, value.Y, value.Z, value.W);
}
