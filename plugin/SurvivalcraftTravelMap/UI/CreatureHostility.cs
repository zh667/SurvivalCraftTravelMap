using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SurvivalcraftTravelMap.UI;

/// <summary>
/// Finds out whether a creature is after the player without knowing its mod: any of its components
/// that keeps a creature or body in an instance field named like a target (target, prey, victim)
/// and currently holds the player there. The game's own chase behaviour (m_target) is one such
/// field; mod creatures without it (the Ghoul mod's shooting corpse head) usually have their own.
/// The field list is reflected once per component type; a frame only reads the fields.
/// The game's own behaviours are understood and mostly harmless when they hold the player (a
/// creature avoiding, staring at, following or moving away from the player keeps it in m_target
/// too), so fields declared in the game's assembly are skipped except for the chase behaviour's.
/// </summary>
public sealed class CreatureTargetProbe
{
    private static readonly string[] TargetWords = ["target", "prey", "victim"];

    private readonly Type[] _targetTypes;
    private readonly Assembly? _gameAssembly;
    private readonly Type[] _gameTypesToProbe;
    private readonly ConcurrentDictionary<Type, FieldInfo[]> _fields = new();

    /// <param name="targetTypes">Field types that can hold the player (ComponentCreature, ComponentBody).</param>
    public CreatureTargetProbe(params Type[] targetTypes)
        : this(targetTypes, gameAssembly: null, gameTypesToProbe: [])
    {
    }

    /// <param name="targetTypes">Field types that can hold the player (ComponentCreature, ComponentBody).</param>
    /// <param name="gameAssembly">Fields declared in this assembly are skipped ...</param>
    /// <param name="gameTypesToProbe">... except those declared by these types (the chase behaviour).</param>
    public CreatureTargetProbe(Type[] targetTypes, Assembly? gameAssembly, Type[] gameTypesToProbe)
    {
        ArgumentNullException.ThrowIfNull(targetTypes);
        ArgumentNullException.ThrowIfNull(gameTypesToProbe);
        _targetTypes = targetTypes;
        _gameAssembly = gameAssembly;
        _gameTypesToProbe = gameTypesToProbe;
    }

    public static bool IsTargetName(string fieldName)
    {
        foreach (var word in TargetWords)
        {
            if (fieldName.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Instance fields (any visibility, inherited ones included) that may hold the player.</summary>
    public FieldInfo[] GetTargetFields(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        return _fields.GetOrAdd(componentType, FindTargetFields);
    }

    /// <summary>The (component, field) pairs worth reading every frame for one creature.</summary>
    public IReadOnlyList<CreatureTargetSlot> CollectSlots(IEnumerable<object> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var slots = new List<CreatureTargetSlot>();
        foreach (var component in components)
        {
            if (component is null)
            {
                continue;
            }

            foreach (var field in GetTargetFields(component.GetType()))
            {
                slots.Add(new CreatureTargetSlot(component, field));
            }
        }

        return slots;
    }

    public static bool IsAnyTargeting(
        IReadOnlyList<CreatureTargetSlot> slots,
        object? playerCreature,
        object? playerBody)
    {
        foreach (var slot in slots)
        {
            object? value;
            try
            {
                value = slot.Field.GetValue(slot.Component);
            }
            catch (Exception)
            {
                continue;
            }

            if (value is not null
                && (ReferenceEquals(value, playerCreature) || ReferenceEquals(value, playerBody)))
            {
                return true;
            }
        }

        return false;
    }

    private FieldInfo[] FindTargetFields(Type componentType)
    {
        var found = new List<FieldInfo>();
        for (var type = componentType; type is not null && type != typeof(object); type = type.BaseType)
        {
            if (_gameAssembly is not null
                && type.Assembly == _gameAssembly
                && Array.IndexOf(_gameTypesToProbe, type) < 0)
            {
                continue;
            }

            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (IsTargetName(field.Name) && IsTargetType(field.FieldType))
                {
                    found.Add(field);
                }
            }
        }

        return found.ToArray();
    }

    private bool IsTargetType(Type fieldType)
    {
        foreach (var targetType in _targetTypes)
        {
            if (targetType.IsAssignableFrom(fieldType))
            {
                return true;
            }
        }

        return false;
    }
}

public readonly record struct CreatureTargetSlot(object Component, FieldInfo Field);

/// <summary>
/// Creatures that hit the player: the attacker itself shows red for <see cref="RecentAttackSeconds"/>,
/// and its entity template is remembered for the world (one name per line in
/// <see cref="FileName"/> in the world's map cache directory) so that others of its kind show
/// yellow from then on even when the marker rules alone would call them harmless.
/// </summary>
public sealed class CreatureHostilityMemory
{
    public const string FileName = "hostile-templates.txt";
    public const double RecentAttackSeconds = 10;

    private readonly object _sync = new();
    private readonly ConditionalWeakTable<object, StrongBox<double>> _recentAttackers = new();
    private readonly HashSet<string> _templates = new(StringComparer.Ordinal);
    private readonly string? _directory;

    private CreatureHostilityMemory(string? directory, IEnumerable<string> templates)
    {
        _directory = directory;
        _templates.UnionWith(templates);
    }

    /// <summary>Reads the remembered templates; a missing or unreadable file starts empty.</summary>
    public static CreatureHostilityMemory Load(string? directory)
    {
        var templates = new List<string>();
        if (directory is not null)
        {
            try
            {
                var path = Path.Combine(directory, FileName);
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var name = line.Trim();
                        if (name.Length > 0)
                        {
                            templates.Add(name);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new CreatureHostilityMemory(directory, templates);
    }

    public IReadOnlyCollection<string> Templates
    {
        get
        {
            lock (_sync)
            {
                return _templates.ToArray();
            }
        }
    }

    /// <summary>
    /// Notes that <paramref name="attacker"/> hit the player at <paramref name="now"/> (seconds).
    /// Returns true when its template was new and has been saved.
    /// </summary>
    public bool RecordAttack(object attacker, string? templateName, double now)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        string[]? toSave = null;
        lock (_sync)
        {
            _recentAttackers.AddOrUpdate(attacker, new StrongBox<double>(now + RecentAttackSeconds));
            if (!string.IsNullOrWhiteSpace(templateName) && _templates.Add(templateName.Trim()))
            {
                toSave = _templates.Order(StringComparer.Ordinal).ToArray();
            }
        }

        if (toSave is null)
        {
            return false;
        }

        Save(toSave);
        return true;
    }

    public bool IsRecentAttacker(object creature, double now)
    {
        lock (_sync)
        {
            return _recentAttackers.TryGetValue(creature, out var until) && now <= until.Value;
        }
    }

    public bool IsAttackerTemplate(string? templateName)
    {
        if (string.IsNullOrEmpty(templateName))
        {
            return false;
        }

        lock (_sync)
        {
            return _templates.Contains(templateName);
        }
    }

    private void Save(string[] templates)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, FileName);
            var temporary = path + ".tmp";
            File.WriteAllLines(temporary, templates);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The memory still works for this session; the file is retried on the next new template.
        }
    }
}
