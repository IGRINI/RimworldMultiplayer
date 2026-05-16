using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

public class FindThingByIdWindow : AbstractTextInputWindow
{
    public FindThingByIdWindow()
    {
        title = "Thing ID or LoadID";
        acceptBtnLabel = "Find";
    }

    public override bool Accept()
    {
        if (!TryParseThingId(curText, out int thingId))
        {
            Messages.Message("Enter a thing id, for example 148644 or Thing_Urn148644.", MessageTypeDefOf.RejectInput, false);
            return false;
        }

        ThingLookupResult result = FindThing(thingId);
        if (result == null)
        {
            Messages.Message($"Thing {thingId} was not found on loaded maps.", MessageTypeDefOf.RejectInput, false);
            Log.Warning($"Thing {thingId} was not found on loaded maps.");
            return false;
        }

        Thing jumpTarget = result.Thing.Spawned ? result.Thing : result.Root;
        CameraJumper.TryJumpAndSelect(jumpTarget);
        Find.Selector.ClearSelection();
        Find.Selector.Select(jumpTarget);

        string message = Describe(result, jumpTarget);
        Log.Message(message);
        Messages.Message(message, jumpTarget, MessageTypeDefOf.SilentInput, false);

        Close();
        return true;
    }

    public override bool Validate(string str)
    {
        return str.Length <= 80;
    }

    private static bool TryParseThingId(string input, out int thingId)
    {
        thingId = 0;
        if (input.NullOrEmpty())
            return false;

        input = input.Trim();
        if (int.TryParse(input, out thingId))
            return true;

        int start = input.Length;
        while (start > 0 && char.IsDigit(input[start - 1]))
            start--;

        return start < input.Length && int.TryParse(input.Substring(start), out thingId);
    }

    private static ThingLookupResult FindThing(int thingId)
    {
        HashSet<Thing> seen = new();

        foreach (Map map in Find.Maps)
        {
            foreach (Thing root in map.listerThings.AllThings.ToList())
            {
                ThingLookupResult result = FindInThingTree(root, root, map, root.ToStringSafe(), thingId, seen);
                if (result != null)
                    return result;
            }
        }

        return null;
    }

    private static ThingLookupResult FindInThingTree(Thing thing, Thing root, Map map, string path, int thingId, HashSet<Thing> seen)
    {
        if (thing == null || !seen.Add(thing))
            return null;

        if (thing.thingIDNumber == thingId)
            return new ThingLookupResult(thing, root, map, path);

        if (thing is not IThingHolder holder)
            return null;

        ThingOwner heldThings = holder.GetDirectlyHeldThings();
        if (heldThings == null)
            return null;

        foreach (Thing heldThing in heldThings)
        {
            ThingLookupResult result = FindInThingTree(heldThing, root, map, $"{path} -> {heldThing.ToStringSafe()}", thingId, seen);
            if (result != null)
                return result;
        }

        return null;
    }

    private static string Describe(ThingLookupResult result, Thing jumpTarget)
    {
        Thing thing = result.Thing;
        string position = thing.Spawned ? thing.Position.ToString() : $"held by {result.Root.ToStringSafe()}";
        string selected = ReferenceEquals(thing, jumpTarget) ? "" : $"; selected parent {jumpTarget.ToStringSafe()}";

        return $"Found {thing.GetUniqueLoadID()} ({thing.LabelCap}) on map {result.Map.Index}/{result.Map.uniqueID} at {position}{selected}. Path: {result.Path}";
    }

    private sealed class ThingLookupResult
    {
        public readonly Thing Thing;
        public readonly Thing Root;
        public readonly Map Map;
        public readonly string Path;

        public ThingLookupResult(Thing thing, Thing root, Map map, string path)
        {
            Thing = thing;
            Root = root;
            Map = map;
            Path = path;
        }
    }
}
