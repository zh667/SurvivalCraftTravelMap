using System.Text;
using System.Xml.Linq;
using Game;
using SurvivalcraftTravelMap.Mod;
using Xunit;

namespace SurvivalcraftTravelMap.Tests;

public sealed class DatabaseInjectionTests
{
    [Fact]
    public void Mod_loader_reads_netxdb_and_attaches_the_component_to_real_database_anchors()
    {
        var source = File.ReadAllBytes(TestPaths.Xdb);
        var entity = new InMemoryModEntity(source);
        var loader = new TravelMapModLoader { Entity = entity };
        var database = CreateBaseDatabase();

        loader.OnXdbLoad(database);

        Assert.Equal(".netxdb", entity.RequestedExtension);
        var player = FindByGuid(database, "4be6c1c5-d65d-4537-8a8b-a391969e6dc2");
        var gameplay = FindByGuid(database, "d3d4b692-acc9-4128-9b99-a5acf1de1fbb");
        var member = Assert.Single(player.Elements("MemberComponentTemplate"));
        var component = Assert.Single(gameplay.Elements("ComponentTemplate"));
        Assert.Equal("TravelMap", member.Attribute("Name")?.Value);
        Assert.Equal("4b67335f-9888-4824-9f0e-cc5f72204b8e", member.Attribute("InheritanceParent")?.Value);
        Assert.Equal("TravelMap", component.Attribute("Name")?.Value);
        Assert.Equal("4b67335f-9888-4824-9f0e-cc5f72204b8e", component.Attribute("Guid")?.Value);
    }

    [Fact]
    public void Missing_base_anchor_leaves_the_database_unmodified_instead_of_partially_injected()
    {
        var entity = new InMemoryModEntity(File.ReadAllBytes(TestPaths.Xdb));
        var loader = new TravelMapModLoader { Entity = entity };
        var database = CreateBaseDatabase(includeGameplay: false);
        var before = new XElement(database);

        loader.OnXdbLoad(database);

        Assert.True(XNode.DeepEquals(before, database));
    }

    [Fact]
    public void Repeated_database_callback_is_idempotent()
    {
        var entity = new InMemoryModEntity(File.ReadAllBytes(TestPaths.Xdb));
        var loader = new TravelMapModLoader { Entity = entity };
        var database = CreateBaseDatabase();

        loader.OnXdbLoad(database);
        loader.OnXdbLoad(database);

        var player = FindByGuid(database, "4be6c1c5-d65d-4537-8a8b-a391969e6dc2");
        var gameplay = FindByGuid(database, "d3d4b692-acc9-4128-9b99-a5acf1de1fbb");
        Assert.Single(player.Elements("MemberComponentTemplate"));
        Assert.Single(gameplay.Elements("ComponentTemplate"));
    }

    private static XElement CreateBaseDatabase(bool includeGameplay = true)
    {
        var databaseObjects = new XElement(
            "DatabaseObjects",
            new XElement(
                "EntityTemplate",
                new XAttribute("Name", "Player"),
                new XAttribute("Guid", "4be6c1c5-d65d-4537-8a8b-a391969e6dc2")));
        if (includeGameplay)
        {
            databaseObjects.Add(new XElement(
                "Folder",
                new XAttribute("Name", "Gameplay"),
                new XAttribute("Guid", "d3d4b692-acc9-4128-9b99-a5acf1de1fbb")));
        }

        return new XElement("Database", databaseObjects);
    }

    private static XElement FindByGuid(XElement root, string guid) =>
        root.DescendantsAndSelf().Single(element => string.Equals(
            element.Attribute("Guid")?.Value,
            guid,
            StringComparison.OrdinalIgnoreCase));

    private sealed class InMemoryModEntity(byte[] xdb) : ModEntity
    {
        internal string? RequestedExtension { get; private set; }

        public override void GetFiles(string extension, Action<string, Stream> action)
        {
            RequestedExtension = extension;
            using var stream = new MemoryStream(xdb, writable: false);
            action("mod.netxdb", stream);
        }
    }
}
