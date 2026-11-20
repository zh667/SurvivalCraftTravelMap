using System.Runtime.CompilerServices;
using Game;
using SurvivalcraftTravelMap.UI;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

/// <summary>
/// Mod creatures the marker rules do not know by name (the Ghoul mod's shooting corpse head has no
/// chase behaviour) are judged by what they do: a component that holds the player as its target,
/// prey or victim shows the creature red, and so does having hit the player; its template is then
/// remembered and shows yellow while not chasing.
/// </summary>
public sealed class CreatureHostilityTests
{
    private static readonly ComponentCreature PlayerCreature = Uninitialized<ComponentPlayer>();
    private static readonly ComponentBody PlayerBody = Uninitialized<ComponentBody>();

    private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static CreatureTargetProbe Probe() => new(typeof(ComponentCreature), typeof(ComponentBody));

    [Fact]
    public void Probe_finds_creature_or_body_fields_named_target_prey_or_victim_including_private_and_inherited()
    {
        var names = Probe().GetTargetFields(typeof(FakeShooter)).Select(f => f.Name).Order().ToArray();

        Assert.Equal(["<Prey>k__BackingField", "LastVictim", "m_baseTarget", "m_target"], names);
    }

    [Fact]
    public void Fields_declared_in_the_game_assembly_are_skipped_except_for_the_chase_behaviour()
    {
        // Stand-in: this test assembly plays the game's, FakeBrainBase its chase behaviour.
        var probe = new CreatureTargetProbe(
            [typeof(ComponentCreature), typeof(ComponentBody)],
            typeof(CreatureHostilityTests).Assembly,
            [typeof(FakeBrainBase)]);

        Assert.Equal(["m_baseTarget"], probe.GetTargetFields(typeof(FakeShooter)).Select(f => f.Name).ToArray());
    }

    [Fact]
    public void Real_game_avoid_and_stare_behaviours_are_not_read_but_the_chase_behaviour_is()
    {
        var probe = new CreatureTargetProbe(
            [typeof(ComponentCreature), typeof(ComponentBody)],
            typeof(ComponentCreature).Assembly,
            [typeof(ComponentChaseBehavior)]);

        Assert.Empty(probe.GetTargetFields(typeof(ComponentAvoidPlayerBehavior)));
        Assert.Empty(probe.GetTargetFields(typeof(ComponentStareBehavior)));
        Assert.Contains(probe.GetTargetFields(typeof(ComponentChaseBehavior)), f => f.Name == "m_target");
    }

    [Fact]
    public void Probe_caches_the_field_list_per_component_type()
    {
        var probe = Probe();

        Assert.Same(probe.GetTargetFields(typeof(FakeShooter)), probe.GetTargetFields(typeof(FakeShooter)));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("prey")]
    [InlineData("victim")]
    [InlineData("body")]
    public void Creature_holding_the_player_in_any_target_field_is_chasing(string which)
    {
        var shooter = new FakeShooter();
        switch (which)
        {
            case "target": shooter.SetTarget(PlayerCreature); break;
            case "prey": shooter.Prey = PlayerBody; break;
            case "victim": shooter.LastVictim = PlayerCreature; break;
            case "body": shooter.SetBaseTarget(PlayerCreature); break;
        }

        var probe = Probe();
        var slots = probe.CollectSlots([new object(), shooter]);

        Assert.True(CreatureTargetProbe.IsAnyTargeting(slots, PlayerCreature, PlayerBody));
    }

    [Fact]
    public void Fields_pointing_elsewhere_or_not_named_as_targets_do_not_count()
    {
        var other = Uninitialized<ComponentCreature>();
        var shooter = new FakeShooter { Friend = PlayerCreature, Prey = Uninitialized<ComponentBody>() };
        shooter.SetTarget(other);
        var probe = Probe();

        Assert.False(CreatureTargetProbe.IsAnyTargeting(probe.CollectSlots([shooter]), PlayerCreature, PlayerBody));
        Assert.Empty(probe.CollectSlots([new FakeShooterWithoutTargets()]));
    }

    [Fact]
    public void Attacker_shows_red_for_ten_seconds()
    {
        var memory = CreatureHostilityMemory.Load(directory: null);
        var head = new object();

        memory.RecordAttack(head, "SAG_CorpsificeHead", now: 100);

        Assert.True(memory.IsRecentAttacker(head, 100));
        Assert.True(memory.IsRecentAttacker(head, 109.9));
        Assert.False(memory.IsRecentAttacker(head, 110.1));
        Assert.False(memory.IsRecentAttacker(new object(), 101));
    }

    [Fact]
    public void Attacker_template_is_remembered_per_world_and_survives_reloading()
    {
        using var directory = new TemporaryDirectory();
        var memory = CreatureHostilityMemory.Load(directory.Path);

        Assert.True(memory.RecordAttack(new object(), "SAG_CorpsificeHead", 5));
        Assert.False(memory.RecordAttack(new object(), "SAG_CorpsificeHead", 6));
        Assert.False(memory.RecordAttack(new object(), "", 7));

        var reloaded = CreatureHostilityMemory.Load(directory.Path);
        Assert.True(reloaded.IsAttackerTemplate("SAG_CorpsificeHead"));
        Assert.False(reloaded.IsAttackerTemplate("Cow_Brown"));
        Assert.Equal(
            ["SAG_CorpsificeHead"],
            File.ReadAllLines(Path.Combine(directory.Path, CreatureHostilityMemory.FileName)));
    }

    [Theory]
    [InlineData(CreatureMarkerAttitude.Passive, false, true, CreatureMarkerAttitude.Provokable)]
    [InlineData(CreatureMarkerAttitude.Passive, false, false, CreatureMarkerAttitude.Passive)]
    [InlineData(CreatureMarkerAttitude.Passive, true, true, CreatureMarkerAttitude.Chasing)]
    [InlineData(CreatureMarkerAttitude.Friendly, false, true, CreatureMarkerAttitude.Friendly)]
    [InlineData(CreatureMarkerAttitude.Friendly, true, false, CreatureMarkerAttitude.Chasing)]
    [InlineData(CreatureMarkerAttitude.Provokable, false, true, CreatureMarkerAttitude.Provokable)]
    public void Frame_marker_combines_chasing_and_remembered_attacker_templates(
        CreatureMarkerAttitude cached,
        bool chasingNow,
        bool attackerTemplate,
        CreatureMarkerAttitude expected)
    {
        var kind = new CreatureMapMarkerKind(cached, CreatureMarkerBody.Land);

        Assert.Equal(expected, CreatureMarkerClassifier.ForFrame(kind, chasingNow, attackerTemplate).Attitude);
    }

    private class FakeBrainBase
    {
        private ComponentCreature? m_baseTarget;

        public void SetBaseTarget(ComponentCreature value) => m_baseTarget = value;
    }

    private sealed class FakeShooter : FakeBrainBase
    {
        private ComponentCreature? m_target;

        public ComponentBody? Prey { get; set; }

        public ComponentCreature? LastVictim;

        public ComponentCreature? Friend;

        public object? m_targetObject;

        public float m_targetRange;

        public void SetTarget(ComponentCreature value) => m_target = value;
    }

    private sealed class FakeShooterWithoutTargets
    {
        public ComponentCreature? Owner;
    }
}
