using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// The touch and embrace mechanics as engine features, tested against
/// a neutral fixture: part-targeted touching with reaction effects
/// (welcome/hesitate/refuse), self-touch, the motive simulation
/// behind it (arousal/pleasure/comfort, climax), ongoing pair states
/// (a fleeting hug, a joined kind with positions and sub-actions, an
/// asymmetric lap), chatter objects, and exit endings. Scenario prose
/// lives in scenarios — here everything is gender-neutral boilerplate.
/// </summary>
public class TouchEmbraceTests
{
    private const string ModulesJson = """
    [ { "id": "room", "name": "Room", "fields": [], "affordances": [] },
      { "id": "agent", "name": "Agent",
        "fields": [ { "name": "policy", "type": "string", "default": "player" },
                    { "name": "posture", "type": "string", "default": "standing" },
                    { "name": "activity", "type": "string", "default": "" },
                    { "name": "speakingSoon", "type": "bool", "default": false } ],
        "affordances": [ { "verb": "look", "handler": "look", "repeatBackoff": true },
                         { "verb": "wait", "handler": "wait", "repeatBackoff": true },
                         { "verb": "say", "handler": "say", "speech": true, "prompt": "Say what?" } ] },
      { "id": "pronouns", "name": "Pronouns",
        "fields": [ { "name": "bundle", "type": "string", "default": "" },
                    { "name": "subject", "type": "string", "default": "" },
                    { "name": "object", "type": "string", "default": "" },
                    { "name": "possessive", "type": "string", "default": "" },
                    { "name": "possessivePronoun", "type": "string", "default": "" },
                    { "name": "reflexive", "type": "string", "default": "" } ],
        "affordances": [] },
      { "id": "female_pronouns", "name": "Female Pronouns",
        "fields": [ { "name": "subject", "type": "string", "default": "she" },
                    { "name": "object", "type": "string", "default": "her" },
                    { "name": "possessive", "type": "string", "default": "her" },
                    { "name": "possessivePronoun", "type": "string", "default": "hers" },
                    { "name": "reflexive", "type": "string", "default": "herself" } ],
        "affordances": [] },
      { "id": "male_pronouns", "name": "Male Pronouns",
        "fields": [ { "name": "subject", "type": "string", "default": "he" },
                    { "name": "object", "type": "string", "default": "him" },
                    { "name": "possessive", "type": "string", "default": "his" },
                    { "name": "possessivePronoun", "type": "string", "default": "his" },
                    { "name": "reflexive", "type": "string", "default": "himself" } ],
        "affordances": [] },
      { "id": "body", "name": "Body",
        "fields": [ { "name": "regions", "type": "list", "default": ["top", "hips", "thighs", "sheath"] } ],
        "affordances": [] },
      { "id": "bodypart", "name": "Body Part",
        "fields": [ { "name": "region", "type": "string", "default": "" },
                    { "name": "intimate", "type": "bool", "default": false },
                    { "name": "sensitivity", "type": "number", "default": 1.0 },
                    { "name": "probe", "type": "string", "default": "" },
                    { "name": "receives", "type": "list", "default": [] } ],
        "affordances": [] },
      { "id": "wearable", "name": "Wearable",
        "fields": [ { "name": "regions", "type": "list", "default": [] },
                    { "name": "worn", "type": "bool", "default": false },
                    { "name": "layer", "type": "string", "default": "" } ],
        "affordances": [ { "verb": "wear", "handler": "wear", "duration": 4,
                           "gates": [ { "kind": "field", "args": { "on": "target", "module": "condom",
                                       "field": "filled", "equals": false },
                                       "failText": "The condom is spent." } ] },
                         { "verb": "remove", "handler": "remove", "duration": 3,
                           "reaction": { "window": 3, "telegraph": "{agent} reaches for the {target}!",
                             "options": [
                               { "id": "let_you", "label": "Let them", "noResist": true, "effect": "welcome",
                                 "defaultWhen": { "module": "intimacy", "field": "comfort", "min": 60 },
                                 "text": "You let them.", "report": "{agent} lets you." },
                               { "id": "stop_you", "label": "Catch their hand", "noResist": true, "default": true,
                                 "effect": "refuse",
                                 "text": "You catch their hand.", "report": "{agent} catches your hand." } ] } } ] },
      { "id": "portable", "name": "Portable", "fields": [],
        "affordances": [ { "verb": "take", "handler": "take" },
                         { "verb": "drop", "handler": "drop", "duration": 1 },
                         { "verb": "give", "handler": "give", "duration": 2,
                           "signals": [ { "sense": "visual", "priority": 5,
                                          "text": "{agent} hands the {item} to {target}." } ] } ] },
      { "id": "toy", "name": "Toy",
        "fields": [ { "name": "probe", "type": "string", "default": "" } ],
        "affordances": [] },
      { "id": "mess", "name": "Mess", "fields": [], "affordances": [] },
      { "id": "tissue", "name": "Tissue",
        "fields": [ { "name": "spent", "type": "bool", "default": false },
                    { "name": "spentName", "type": "string", "default": "" } ],
        "affordances": [ { "verb": "discard", "handler": "destroy",
                           "label": "Throw the {target} away",
                           "when": [ { "module": "tissue", "field": "spent", "equals": true } ],
                           "data": { "self": "You toss the {target}." } } ] },
      { "id": "showermod", "name": "Shower",
        "fields": [],
        "affordances": [ { "verb": "shower", "handler": "wash", "duration": 40,
                           "label": "Take a shower",
                           "data": { "messModule": "mess",
                                     "self": "You let the hot water carry it all away." } } ] },
      { "id": "sittable", "name": "Sittable",
        "fields": [ { "name": "capacity", "type": "int", "default": 2 } ],
        "affordances": [ { "verb": "sit", "handler": "sit", "duration": 2,
                           "signals": [ { "sense": "visual", "priority": 5,
                                          "text": "{agent} sits down on the {target}." } ] } ] },
      { "id": "lyable", "name": "Lyable",
        "fields": [ { "name": "capacity", "type": "int", "default": 2 } ],
        "affordances": [ { "verb": "lie", "handler": "lie", "duration": 2,
                           "label": "Lie down on the {target}" } ] },
      { "id": "intimacy", "name": "Intimacy",
        "fields": [ { "name": "horniness", "type": "number", "default": 0.5 },
                    { "name": "arousal", "type": "number", "default": 0.0 },
                    { "name": "pleasure", "type": "number", "default": 0.0 },
                    { "name": "frustration", "type": "number", "default": 0.0 },
                    { "name": "comfort", "type": "number", "default": 50.0 },
                    { "name": "motives", "type": "list", "default": [
                      { "id": "arousal",
                        "drift": [ { "mode": "proportional", "target": "horniness", "rate": 0.002 } ],
                        "bands": [ { "min": 0.45, "condition": "cond_flushed" } ] },
                      { "id": "pleasure",
                        "drift": [ { "mode": "linear", "target": 0, "rate": 0.006 } ],
                        "onFull": { "id": "climax",
                          "set": { "pleasure": 0, "arousal": 0.15, "frustration": 0 },
                          "adjust": { "horniness": -0.1 },
                          "attach": [ "cond_satisfied" ],
                          "self": "It crests and lets go.",
                          "signal": { "sense": "audible", "priority": 8, "salience": 14,
                                      "text": "{agent} shudders, holding on." },
                          "silentBands": true,
                          "emit": { "template": "tpl_semen", "probe": "penis",
                                    "collectModule": "condom" } } },
                      { "id": "frustration",
                        "drift": [
                          { "when": [ { "field": "arousal", "min": 0.5 },
                                      { "field": "pleasure", "max": 0.2 } ], "rate": 0.0012 },
                          { "rate": -0.002 } ] },
                      { "id": "comfort", "min": 0, "max": 100 },
                      { "id": "horniness" } ] } ],
        "affordances": [] },
      { "id": "condition", "name": "Condition",
        "fields": [ { "name": "kind", "type": "string", "default": "" },
                    { "name": "label", "type": "string", "default": "" },
                    { "name": "visible", "type": "bool", "default": true },
                    { "name": "selfText", "type": "string", "default": "" },
                    { "name": "clearText", "type": "string", "default": "" },
                    { "name": "traits", "type": "string", "default": "" } ],
        "affordances": [] },
      { "id": "embrace", "name": "Embrace",
        "fields": [ { "name": "kind", "type": "string", "default": "" },
                    { "name": "members", "type": "list", "default": [] },
                    { "name": "positions", "type": "list", "default": [] },
                    { "name": "position", "type": "string", "default": "" },
                    { "name": "entryPosture", "type": "string", "default": "" },
                    { "name": "exposeRequired", "type": "string", "default": "" },
                    { "name": "autoEnd", "type": "bool", "default": false },
                    { "name": "occupiesA", "type": "list", "default": [] },
                    { "name": "occupiesB", "type": "list", "default": [] } ],
        "affordances": [] },
      { "id": "chatter", "name": "Chatter",
        "fields": [ { "name": "on", "type": "bool", "default": false },
                    { "name": "channel", "type": "string", "default": "news" },
                    { "name": "channels", "type": "map", "default": {} },
                    { "name": "interval", "type": "number", "default": 40.0 },
                    { "name": "elapsed", "type": "int", "default": 0 },
                    { "name": "nextDue", "type": "int", "default": 0 } ],
        "affordances": [ { "verb": "turnon", "handler": "set", "duration": 2,
                           "label": "Turn on the {target}",
                           "when": [ { "module": "chatter", "field": "on", "equals": false } ],
                           "data": { "module": "chatter", "field": "on", "value": "true",
                                     "self": "{target} starts up." } } ] },
      { "id": "touch", "name": "Touch", "fields": [],
        "affordances": [
          { "verb": "kiss", "handler": "touch", "targetParts": true, "duration": 3,
            "data": { "self": "You kiss {target}.",
                      "impulse.arousal": "0.045", "impulse.pleasure": "0.03", "impulse.comfort": "2",
                      "sensitivityScales": "arousal,pleasure",
                      "giver.impulse.arousal": "0.015",
                      "melt.impulse.comfort": "1", "stop.impulse.comfort": "-2",
                      "usesParts": "lips",
                      "onStop": "They turn aside, gently." },
            "gates": [ { "kind": "partsFree", "failText": "Your mouth is otherwise spoken for." } ],
            "reaction": { "window": 3, "telegraph": "{agent} leans in to kiss {holder}.",
              "options": [
                { "id": "melt", "label": "Melt into it", "noResist": true, "effect": "welcome",
                  "defaultWhen": { "module": "intimacy", "field": "comfort", "min": 55 },
                  "text": "You melt into it.", "report": "{agent} melts into it." },
                { "id": "stop", "label": "Ease back", "noResist": true, "default": true, "effect": "refuse",
                  "text": "You ease back.", "report": "{agent} eases back." } ] },
            "signals": [ { "sense": "visual", "priority": 8, "audience": "onlyTarget",
                           "text": "{agent} kisses your {target}." },
                         { "sense": "visual", "priority": 6,
                           "text": "{agent} kisses {holder}'s {target}." } ] },
          { "verb": "stroke", "handler": "touch", "targetParts": true, "intimateParts": true,
            "duration": 4,
            "data": { "self": "You stroke {target}.",
                      "impulse.arousal": "0.075", "impulse.pleasure": "0.045",
                      "sensitivityScales": "arousal,pleasure" },
            "gates": [ { "kind": "exposed", "failText": "Clothes are in the way of that." } ],
            "reaction": { "window": 3, "telegraph": "{agent}'s hand drifts toward {holder}.",
              "options": [
                { "id": "melt", "label": "Press into the touch", "noResist": true, "effect": "welcome",
                  "defaultWhen": { "module": "intimacy", "field": "arousal", "min": 0.4 },
                  "text": "You press into it.", "report": "{agent} presses into it." },
                { "id": "stop", "label": "Still their hand", "noResist": true, "default": true, "effect": "refuse",
                  "text": "You still their hand.", "report": "{agent} stills your hand." } ] } },
          { "verb": "massage", "handler": "touch", "targetParts": true, "selfParts": true, "duration": 10,
            "data": { "self": "You massage {target}.", "impulse.comfort": "5" } },
          { "verb": "rub", "handler": "touch", "targetParts": true, "intimateParts": true,
            "coveredParts": true, "duration": 6,
            "label": "Rub {holder}'s {part} through the fabric",
            "data": { "self": "You rub {target} through the fabric.",
                      "impulse.comfort": "2" },
            "gates": [ { "kind": "covered", "failText": "There is nothing in the way of that." } ] },
          { "verb": "finger", "handler": "touch", "targetParts": true, "intimateParts": true,
            "duration": 6,
            "data": { "self": "You trace {target} with your {instrument}.",
                      "probe": "fingers",
                      "impulse.arousal": "0.06", "impulse.pleasure": "0.04",
                      "sensitivityScales": "arousal,pleasure",
                      "onNoInstrument": "You are out of fingers, somehow.",
                      "onIncompatible": "That isn't a way bodies fit.",
                      "onCoveredInstrument": "Your own clothes are in the way." },
            "gates": [ { "kind": "exposed", "failText": "Clothes are in the way of that." },
                       { "kind": "partsFree", "failText": "That isn't free right now." } ] },
          { "verb": "grind", "handler": "touch", "targetParts": true, "intimateParts": true,
            "duration": 8,
            "data": { "self": "You press your {instrument} to {target}.",
                      "probe": "penis",
                      "impulse.arousal": "0.08", "impulse.pleasure": "0.05",
                      "onNoInstrument": "You lack the equipment.",
                      "onIncompatible": "That isn't a way bodies fit.",
                      "onCoveredInstrument": "Your own clothes are in the way of that." },
            "gates": [ { "kind": "exposed", "failText": "Clothes are in the way of that." },
                       { "kind": "partsFree", "failText": "That isn't free right now." } ] },
          { "verb": "tease", "handler": "touch", "targetParts": true, "intimateParts": true,
            "duration": 6,
            "data": { "self": "You tease {target} with the {instrument}.",
                      "probe": "silicone",
                      "impulse.pleasure": "0.05",
                      "onNoInstrument": "You have nothing of the sort." },
            "gates": [ { "kind": "exposed", "failText": "Clothes are in the way of that." } ] },
          { "verb": "suck", "handler": "touch", "targetParts": true, "intimateParts": true,
            "duration": 6,
            "label": "Suck on {holder}'s {part}",
            "data": { "self": "You take {target} into your mouth, warm and slow.",
                      "targetsProbes": "penis",
                      "impulse.pleasure": "0.06",
                      "usesParts": "lips",
                      "onIncompatible": "That isn't a way bodies fit." },
            "gates": [ { "kind": "partsFree", "failText": "Your mouth is otherwise spoken for." } ] },
          { "verb": "suck", "handler": "touch", "targetParts": true, "duration": 6,
            "label": "Suck on {holder}'s {part}",
            "data": { "self": "You take {target} into your mouth, warm and slow.",
                      "targetsProbes": "fingers",
                      "impulse.arousal": "0.04", "impulse.comfort": "1",
                      "giver.impulse.arousal": "0.03",
                      "sensitivityScales": "arousal",
                      "usesParts": "lips",
                      "onIncompatible": "That isn't a way bodies fit." },
            "gates": [ { "kind": "partsFree", "failText": "Your mouth is otherwise spoken for." } ] },
          { "verb": "wipe", "handler": "clean", "targetParts": true, "messyParts": true,
            "selfParts": true, "duration": 4,
            "data": { "messModule": "mess", "toolModule": "tissue",
                      "self": "You wipe {target} clean with the {instrument}.",
                      "onNoTool": "You have nothing to wipe with." } },
          { "verb": "swallow", "handler": "clean", "targetParts": true, "messyParts": true,
            "selfOnly": true, "duration": 2, "label": "Swallow the {mess}",
            "data": { "messModule": "mess",
                      "self": "You swallow it down, deliberate and slow." },
            "gates": [ { "kind": "field", "args": { "on": "target", "module": "bodypart",
                        "field": "mouth", "equals": true },
                         "failText": "That isn't somewhere you can swallow from." } ] },
          { "verb": "masturbate", "handler": "touch", "targetParts": true, "intimateParts": true,
            "selfOnly": true, "duration": 10,
            "data": { "self": "You take {target} in hand, breath going with it.",
                      "impulse.arousal": "0.07", "impulse.pleasure": "0.05",
                      "sensitivityScales": "arousal,pleasure",
                      "onEvent": "It crests, and you ride it out." },
            "signals": [ { "sense": "visual", "priority": 6, "salience": 12,
                           "text": "{agent} takes {holder.possessive} {target} in hand, unhurried." } ] } ] },
      { "id": "companion", "name": "Companion", "fields": [],
        "affordances": [
          { "verb": "hug", "handler": "embrace", "othersOnly": true, "duration": 5,
            "label": "Hug {target}",
            "data": { "template": "tpl_hug", "self": "You hold them close, a moment whole.",
                      "impulse.comfort": "4",
                      "onBusy": "They are already holding someone close.",
                      "onStop": "They hang back, not unkindly." },
            "reaction": { "window": 3, "telegraph": "{agent} opens their arms to {target}.",
              "options": [
                { "id": "hold", "label": "Hold on", "noResist": true, "effect": "welcome",
                  "defaultWhen": { "module": "intimacy", "field": "comfort", "min": 50 },
                  "text": "You hold on.", "report": "{agent} holds on." },
                { "id": "pat", "label": "Endure it politely", "noResist": true, "default": true,
                  "effect": "hesitate",
                  "text": "You endure it politely.", "report": "{agent} endures it politely." } ] } },
          { "verb": "lap", "handler": "embrace", "othersOnly": true, "duration": 6,
            "label": "Sit on {target}'s lap",
            "when": [ { "module": "agent", "field": "posture", "equals": "sitting" } ],
            "data": { "template": "tpl_lap", "self": "You settle onto their lap.",
                      "impulse.comfort": "3",
                      "onBusy": "Someone is already held close." } },
          { "verb": "odown", "handler": "embrace", "othersOnly": true, "sameSupport": true,
            "duration": 10, "postures": ["sitting", "lying", "prone"],
            "label": "Go down on {target}",
            "data": { "template": "tpl_oral", "self": "You bow between their thighs, devoted.",
                      "impulse.arousal": "0.1",
                      "onBusy": "You are already held close." } },
          { "verb": "riseup", "handler": "disengage", "othersOnly": true,
            "requiresEmbrace": { "kind": "oral" },
            "duration": 4, "label": "Raise your head",
            "data": { "self": "You raise your head, breath warm." } },
          { "verb": "climbdown", "handler": "disengage", "othersOnly": true,
            "requiresEmbrace": { "kind": "lap" },
            "duration": 3, "label": "Climb off {target}'s lap",
            "data": { "self": "You climb down, mostly with dignity." } },
          { "verb": "join", "handler": "embrace", "othersOnly": true, "sameSupport": true,
            "duration": 8, "postures": ["sitting", "lying"],
            "label": "Draw {target} into your arms",
            "data": { "template": "tpl_joined", "position": "face-to-face",
                      "self": "You draw them close until there is no space left.",
                      "impulse.arousal": "0.15", "impulse.pleasure": "0.05", "impulse.comfort": "5",
                      "onClothedSelf": "Your own clothes are in the way.",
                      "onClothedOther": "Their clothes are still in the way.",
                      "onStop": "They ask to wait a while." },
            "gates": [ { "kind": "field", "args": { "on": "target", "module": "intimacy",
                        "field": "comfort", "min": 55 }, "failText": "Not yet — too soon." },
                       { "kind": "field", "args": { "on": "target", "module": "intimacy",
                        "field": "arousal", "min": 0.4 }, "failText": "The wanting must build first." } ],
            "reaction": { "window": 4, "telegraph": "{agent} draws {target} closer.",
              "options": [
                { "id": "guide", "label": "Guide them in", "noResist": true, "effect": "welcome",
                  "defaultWhen": { "module": "intimacy", "field": "comfort", "min": 65 },
                  "text": "You guide them in.", "report": "{agent} guides you in." },
                { "id": "slow", "label": "Ask to slow down", "noResist": true, "default": true,
                  "effect": "refuse",
                  "text": "You press a palm to their chest.", "report": "{agent} asks to slow down." } ] } },
          { "verb": "thrust", "handler": "touch", "othersOnly": true,
            "requiresEmbrace": { "kind": "joined" },
            "duration": 4, "label": "Move in {target}",
            "data": { "self": "You move together, unhurried.",
                      "self.behind": "You move together, gathered close.",
                      "impulse.arousal": "0.055", "impulse.pleasure": "0.07", "impulse.comfort": "1",
                      "giver.impulse.arousal": "0.05", "giver.impulse.pleasure": "0.085",
                      "pace.behind": "1.1",
                      "onEvent": "They break against you, breath gone, holding on.",
                      "onSelfEvent": "The warmth crests and spends; you hold on." } },
          { "verb": "shift", "handler": "reposition", "othersOnly": true,
            "requiresEmbrace": { "kind": "joined" },
            "duration": 4, "label": "Turn {target} gently away from you",
            "data": { "position": "behind", "self": "You gather them back against your chest.",
                      "impulse.arousal": "0.03", "impulse.comfort": "2" },
            "signals": [ { "sense": "visual", "priority": 5,
                           "text": "{agent} gathers {target} close, turning them." } ] },
          { "verb": "shift", "handler": "reposition", "othersOnly": true,
            "requiresEmbrace": { "kind": "joined" },
            "duration": 4, "label": "Face {target} again",
            "data": { "position": "face-to-face", "self": "You turn to face them once more." },
            "signals": [ { "sense": "visual", "priority": 5,
                           "text": "{agent} turns to face {target} again." } ] },
          { "verb": "spoon", "handler": "reposition", "othersOnly": true,
            "requiresEmbrace": { "kind": "joined" },
            "duration": 4, "label": "Spoon {target}",
            "data": { "position": "floating", "self": "You float together.",
                      "onImpossible": "Bodies do not bend that way." } },
          { "verb": "withdraw", "handler": "disengage", "othersOnly": true,
            "requiresEmbrace": { "kind": "joined" },
            "duration": 5, "label": "Draw {target} close and rest",
            "data": { "self:after": "You ease apart and rest, breathing slowing.",
                      "self:unfinished": "You ease apart; they keep you close, not finished.",
                      "unfinishedKind": "satisfied",
                      "unfinishedModule": "intimacy", "unfinishedField": "arousal",
                      "unfinishedMin": "0.5",
                      "unfinished.impulse.comfort": "-2",
                      "self": "You draw apart." } } ] },
      { "id": "exit", "name": "Exit",
        "fields": [ { "name": "text", "type": "string", "default": "" },
                    { "name": "endings", "type": "map", "default": {} } ],
        "affordances": [ { "verb": "leave", "handler": "leave", "duration": 5,
                           "playerOnly": true, "label": "Go home" } ] }
    ]
    """;

    private static GameEngine NewFixture()
    {
        var engine = TestWorlds.NewEngine();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        var world = engine.World;
        world.CreateObject("room_a", World.RootId, "Room A");
        world.AddModule("room_a", "room");
        world.CreateObject("sofa", "room_a", "sofa");
        world.AddModule("sofa", "sittable");
        world.AddModule("sofa", "lyable");
        world.CreateObject("tv", "room_a", "radio");
        world.AddModule("tv", "chatter");
        world.SetFieldOverride("tv", "chatter", "interval", World.ToJson(40.0));
        world.SetFieldOverride("tv", "chatter", "channels", World.ToJson(
            new Dictionary<string, List<string>>
            {
                ["news"] = ["…the council deferred the vote again…", "…stocks closed mixed…"],
                ["test"] = ["a test jingle plays."],
            }));
        world.CreateObject("exitdoor", "room_a", "front door");
        world.AddModule("exitdoor", "exit");
        world.SetFieldOverride("exitdoor", "exit", "text", World.ToJson("You say your goodbyes."));
        world.SetFieldOverride("exitdoor", "exit", "endings", World.ToJson(
            new Dictionary<string, string> { ["satisfied"] = "They wave until the corner." }));

        foreach (var (id, comfort) in new[] { ("player", 45.0), ("sam", 52.0) })
        {
            world.CreateObject(id, "room_a", id == "player" ? "Riley" : "Sam");
            world.AddModule(id, "agent");
            world.AddModule(id, "pronouns");
            world.SetFieldOverride(id, "pronouns", "bundle",
                World.ToJson(id == "player" ? "male_pronouns" : "female_pronouns"));
            world.AddModule(id, "touch");
            world.AddModule(id, "body");
            world.AddModule(id, "intimacy");
            world.AddModule(id, "companion");
            world.SetFieldOverride(id, "intimacy", "comfort", World.ToJson(comfort));
            world.SetFieldOverride(id, "intimacy", "horniness", World.ToJson(0.6));
            world.SetFieldOverride(id, "intimacy", "arousal", World.ToJson(0.2));
        }
        world.SetFieldOverride("sam", "agent", "policy", World.ToJson("auto"));

        // embrace templates at the root
        void EmbraceTemplate(string id, string kind, bool autoEnd,
            string[]? positions = null, string? entryPosture = null, string? exposeRequired = null,
            string[]? occupiesA = null, string[]? occupiesB = null)
        {
            world.CreateObject(id, World.RootId, $"{kind} template");
            world.AddModule(id, "embrace");
            world.SetFieldOverride(id, "embrace", "kind", World.ToJson(kind));
            world.SetFieldOverride(id, "embrace", "autoEnd", World.ToJson(autoEnd));
            if (positions is not null)
                world.SetFieldOverride(id, "embrace", "positions", World.ToJson(positions));
            if (entryPosture is not null)
                world.SetFieldOverride(id, "embrace", "entryPosture", World.ToJson(entryPosture));
            if (exposeRequired is not null)
                world.SetFieldOverride(id, "embrace", "exposeRequired", World.ToJson(exposeRequired));
            if (occupiesA is not null)
                world.SetFieldOverride(id, "embrace", "occupiesA", World.ToJson(occupiesA));
            if (occupiesB is not null)
                world.SetFieldOverride(id, "embrace", "occupiesB", World.ToJson(occupiesB));
        }
        EmbraceTemplate("tpl_hug", "hug", autoEnd: true);
        // emission template (its clones carry the mess module, so the
        // cleanup family can find them) and a collecting garment
        world.CreateObject("tpl_semen", World.RootId, "semen");
        world.AddModule("tpl_semen", "mess");
        world.CreateObject("tissue1", "room_a", "tissue");
        world.AddModule("tissue1", "portable");
        world.AddModule("tissue1", "tissue");
        world.SetFieldOverride("tissue1", "tissue", "spentName", World.ToJson("soiled tissue"));
        world.CreateObject("shower", "room_a", "shower stall");
        world.AddModule("shower", "showermod");
        world.CreateObject("condom", "room_a", "condom");
        world.AddModule("condom", "portable");
        world.AddModule("condom", "wearable");
        world.AddModule("condom", "condom");
        // the condom occupies its own region — it must not COVER the
        // sex for exposure purposes
        world.SetFieldOverride("condom", "wearable", "regions", World.ToJson(new[] { "sheath" }));
        world.SetFieldOverride("condom", "wearable", "layer", World.ToJson("condom"));
        world.SetFieldOverride("condom", "wearable", "worn", World.ToJson(false));
        world.SetFieldOverride("condom", "condom", "filledName", World.ToJson("filled condom"));
        world.SetFieldOverride("condom", "condom", "fillRegions", World.ToJson(new[] { "hips" }));
        EmbraceTemplate("tpl_lap", "lap", autoEnd: false);
        EmbraceTemplate("tpl_oral", "oral", autoEnd: false,
            occupiesA: ["lips"], occupiesB: ["sex"]);
        EmbraceTemplate("tpl_joined", "joined", autoEnd: false,
            positions: ["face-to-face", "behind"], entryPosture: "lying", exposeRequired: "hips",
            occupiesA: ["sex"], occupiesB: ["sex"]);

        // condition templates at the root
        foreach (var (id, kind) in new[] { ("cond_flushed", "flushed"), ("cond_satisfied", "satisfied") })
        {
            world.CreateObject(id, World.RootId, id);
            world.AddModule(id, "condition");
            world.SetFieldOverride(id, "condition", "kind", World.ToJson(kind));
        }

        // parts: neutral anatomy, intimate ones under wear regions;
        // probes (fingers, penis) and receives lists (orifices) drive
        // the penetrate family
        foreach (var (owner, part, region, intimate, sens) in new[]
                 {
                     ("player", "lips", "", false, 1.5), ("player", "neck", "", false, 1.3),
                     ("player", "chest", "top", false, 1.0), ("player", "sex", "hips", true, 2.5),
                     ("player", "fingers", "", false, 1.0),
                     ("sam", "lips", "", false, 1.6), ("sam", "neck", "", false, 1.5),
                     ("sam", "shoulders", "", false, 1.0), ("sam", "chest", "top", true, 2.2),
                     ("sam", "thighs", "thighs", true, 1.8), ("sam", "sex", "hips", true, 3.0),
                     ("sam", "fingers", "", false, 1.0),
                 })
        {
            var id = $"{owner}_{part}";
            world.CreateObject(id, owner, part);
            world.AddModule(id, "bodypart");
            if (region.Length > 0)
                world.SetFieldOverride(id, "bodypart", "region", World.ToJson(region));
            world.SetFieldOverride(id, "bodypart", "intimate", World.ToJson(intimate));
            world.SetFieldOverride(id, "bodypart", "sensitivity", World.ToJson(sens));
        }
        // part roles: instruments and orifices
        world.SetFieldOverride("player_fingers", "bodypart", "probe", World.ToJson("fingers"));
        world.SetFieldOverride("sam_fingers", "bodypart", "probe", World.ToJson("fingers"));
        world.SetFieldOverride("player_sex", "bodypart", "probe", World.ToJson("penis"));
        world.SetFieldOverride("sam_lips", "bodypart", "receives",
            World.ToJson(new[] { "tongue", "penis" }));
        world.SetFieldOverride("sam_lips", "bodypart", "mouth", World.ToJson(true));
        world.SetFieldOverride("player_lips", "bodypart", "mouth", World.ToJson(true));
        world.SetFieldOverride("sam_sex", "bodypart", "receives",
            World.ToJson(new[] { "fingers", "tongue", "penis", "silicone" }));

        // a held toy providing a probe of its own
        world.CreateObject("dildo", "room_a", "smooth toy");
        world.AddModule("dildo", "portable");
        world.AddModule("dildo", "toy");
        world.SetFieldOverride("dildo", "toy", "probe", World.ToJson("silicone"));

        // garments cover their regions; undergarments sit in their own
        // layer so outerwear stacks over them
        foreach (var (id, owner, name, regions, layer) in new[]
                 {
                     ("g_shirt", "player", "shirt", new[] { "top" }, ""),
                     ("g_trousers", "player", "trousers", new[] { "hips", "thighs" }, ""),
                     ("g_shorts", "player", "shorts", new[] { "hips" }, "under"),
                     ("g_sweater", "sam", "sweater", new[] { "top" }, ""),
                     ("g_sam_trousers", "sam", "trousers", new[] { "hips", "thighs" }, ""),
                     ("g_underwear", "sam", "underwear", new[] { "hips" }, "under"),
                 })
        {
            world.CreateObject(id, owner, name);
            world.AddModule(id, "portable");
            world.AddModule(id, "wearable");
            world.SetFieldOverride(id, "wearable", "regions", World.ToJson(regions));
            if (layer.Length > 0)
                world.SetFieldOverride(id, "wearable", "layer", World.ToJson(layer));
            world.SetFieldOverride(id, "wearable", "worn", World.ToJson(true));
        }
        engine.TurnManager.EvaluateUpkeep();
        return engine;
    }

    private static double Field(GameEngine engine, string id, string field) =>
        engine.ModuleRegistry.ResolveDouble(engine.World.GetObject(id), "intimacy", field);

    private static void Set(GameEngine engine, string id, string field, double value) =>
        engine.World.SetFieldOverride(id, "intimacy", field, World.ToJson(value));

    private static WorldObject? EmbraceBetween(GameEngine engine, string a, string b) =>
        Embraces.Find(engine.World, engine.ModuleRegistry,
            engine.World.GetObject(a), engine.World.GetObject(b));

    /// <summary>Perform an action; reactions resolve via the effective default, so the handler outcome reports through the resolved queue.</summary>
    private static (ActionResult Result, bool ViaReaction) Act(
        GameEngine engine, string verb, string? targetId)
    {
        var player = engine.World.GetObject("player");
        var action = engine.ActionResolver.Resolve(player)
            .FirstOrDefault(a => a.Verb == verb && (targetId is null || a.TargetId == targetId));
        if (action is null)
            return (ActionResult.Fail($"no {verb} action"), false);
        var result = engine.TurnManager.PerformAction(player, action);
        var resolved = engine.Reactions.DrainResolved()
            .LastOrDefault(r => r.ActorId == "player");
        if (resolved.Message is { Length: > 0 } message)
            return (ActionResult.Ok(message), true);
        return (result, false);
    }

    [Fact]
    public void TouchListing_HidesIntimatePartsWhileDressed()
    {
        var engine = NewFixture();
        var labels = engine.ActionResolver.Resolve(engine.World.GetObject("player"))
            .Where(a => a.Verb is "kiss" or "massage" or "stroke")
            .Select(a => a.Label).ToList();

        Assert.Contains("Kiss Sam's lips", labels);
        Assert.Contains("Massage Sam's shoulders", labels);
        // sam's intimate parts stay hidden while dressed; the player's
        // own non-intimate parts list for self-touch
        Assert.DoesNotContain(labels, l => l.Contains("Sam's chest"));
        Assert.DoesNotContain(labels, l => l.Contains("thighs"));
        Assert.DoesNotContain(labels, l => l.Contains("sex"));
        Assert.Contains("Massage your own chest", labels);
    }

    [Fact]
    public void SelfTouch_ListsOwnParts_AndImpulsesTheSelf()
    {
        var engine = NewFixture();
        var labels = engine.ActionResolver.Resolve(engine.World.GetObject("player"))
            .Where(a => a.Verb == "massage").Select(a => a.Label).ToList();

        Assert.Contains("Massage your own neck", labels);
        Assert.Contains("Massage Sam's neck", labels);

        var (result, reacted) = Act(engine, "massage", "player_neck");

        Assert.False(reacted); // no reaction to your own touch
        Assert.Equal("You massage your own neck.", result.Message);
        Assert.True(Field(engine, "player", "comfort") > 45.0);
        Assert.Equal(52.0, Field(engine, "sam", "comfort")); // untouched
    }

    [Fact]
    public void DeflectedTouch_AtLowComfort_FailsGentlyAndCools()
    {
        var engine = NewFixture();
        var (result, reacted) = Act(engine, "kiss", "sam_lips");

        Assert.True(reacted);
        Assert.Contains("turn aside", result.Message);
        Assert.True(Field(engine, "sam", "comfort") < 52.0); // a deflection costs warmth
    }

    [Fact]
    public void WelcomedTouch_AtHighComfort_WarmsArouses_AndReachesTheOwner()
    {
        var engine = NewFixture();
        Set(engine, "sam", "comfort", 60);
        var (result, _) = Act(engine, "kiss", "sam_lips");

        Assert.Equal("You kiss Sam's lips.", result.Message);
        Assert.True(Field(engine, "sam", "comfort") > 62.0);
        Assert.True(Field(engine, "sam", "arousal") > 0.27); // 0.2 + 0.045 * 1.6 sensitivity
        // the touched party's private rendering, plus the bystander form
        // observed BY the holder — possessive renders "your", not "you's"
        Assert.Contains(engine.SignalBus.Drain("sam"), s => s.Text == "Riley kisses your lips.");
        var (neck, _) = Act(engine, "kiss", "sam_neck");
        Assert.Contains(engine.SignalBus.Drain("sam"),
            s => s.Text == "Riley kisses your neck.");
    }

    [Fact]
    public void Motives_DriftTowardHorniness_PleasureFades()
    {
        var engine = NewFixture();
        Set(engine, "sam", "pleasure", 0.5);
        Set(engine, "sam", "arousal", 0.1);

        Motives.Advance(engine, 120);

        Assert.True(Field(engine, "sam", "pleasure") < 0.5);
        Assert.True(Field(engine, "sam", "arousal") > 0.1);
    }

    [Fact]
    public void Climax_SatisfiesResets_AndIsObservable()
    {
        var engine = NewFixture();

        var fired = Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            engine.World.GetObject("sam"),
            new Dictionary<string, double> { ["pleasure"] = 1.0 });

        Assert.Equal(["climax"], fired);
        Assert.True(Conditions.Has(engine.World, engine.ModuleRegistry,
            engine.World.GetObject("sam"), "satisfied"));
        Assert.True(Field(engine, "sam", "pleasure") < 0.01);
        Assert.Equal(0.0, Field(engine, "sam", "frustration"));
        Assert.Contains(engine.SignalBus.Drain("player"), s => s.Text.Contains("shudders"));
    }

    [Fact]
    public void Chatter_OffByDefault_EmitsOnlyWhenOn_FromItsChannelPool()
    {
        var engine = NewFixture();
        Chatter.Advance(engine, 500);
        Assert.Empty(engine.SignalBus.Drain("player")); // off

        engine.World.SetFieldOverride("tv", "chatter", "on", World.ToJson(true));
        engine.World.SetFieldOverride("tv", "chatter", "nextDue", World.ToJson(1));
        Chatter.Advance(engine, 5);
        Assert.Single(engine.SignalBus.Drain("player")); // one line per due interval

        engine.World.SetFieldOverride("tv", "chatter", "channel", World.ToJson("test"));
        engine.World.SetFieldOverride("tv", "chatter", "elapsed", World.ToJson(0));
        engine.World.SetFieldOverride("tv", "chatter", "nextDue", World.ToJson(1));
        Chatter.Advance(engine, 5);
        Assert.Equal("a test jingle plays.",
            Assert.Single(engine.SignalBus.Drain("player")).Text);
    }

    private static void JoinThem(GameEngine engine)
    {
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");
        engine.TurnManager.PerformAction(player, TestWorlds.Find(engine, "player", "sit", "sofa"));
        engine.TurnManager.PerformAction(sam, TestWorlds.Find(engine, "sam", "sit", "sofa"));
        Set(engine, "sam", "comfort", 70);
        Set(engine, "sam", "arousal", 0.6);
        foreach (var id in new[] { "g_sweater", "g_sam_trousers", "g_underwear",
                                   "g_trousers", "g_shorts" })
            engine.World.SetFieldOverride(id, "wearable", "worn", World.ToJson(false));
        Act(engine, "join", "sam");
    }

    [Fact]
    public void JoinedEmbrace_GatedByComfortAndClothes_ThenTheFlowRuns()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");
        engine.TurnManager.PerformAction(player, TestWorlds.Find(engine, "player", "sit", "sofa"));
        engine.TurnManager.PerformAction(sam, TestWorlds.Find(engine, "sam", "sit", "sofa"));

        // gates fail loudly before any reaction
        Set(engine, "sam", "comfort", 45);
        var (gated, _) = Act(engine, "join", "sam");
        Assert.Contains("too soon", gated.Message);

        // comfortable but clothed: the garment check fails politely
        Set(engine, "sam", "comfort", 70);
        Set(engine, "sam", "arousal", 0.6);
        var (clothed, _) = Act(engine, "join", "sam");
        Assert.Contains("in the way", clothed.Message);

        // undressed and willing: joined — a shared embrace object at the root
        foreach (var id in new[] { "g_sweater", "g_sam_trousers", "g_underwear",
                                   "g_trousers", "g_shorts" })
            engine.World.SetFieldOverride(id, "wearable", "worn", World.ToJson(false));
        var (joined, _) = Act(engine, "join", "sam");
        Assert.Contains("no space left", joined.Message);
        var embrace = EmbraceBetween(engine, "player", "sam");
        Assert.NotNull(embrace);
        Assert.Equal("joined", Embraces.Kind(engine.ModuleRegistry, embrace!));
        Assert.Equal("face-to-face", Embraces.Position(engine.ModuleRegistry, embrace!));
        Assert.Equal(Postures.Lying, Postures.Of(engine.World, engine.ModuleRegistry, player));
        Assert.True(Field(engine, "player", "pleasure") > 0.0); // entry impulses landed

        // the driver's pleasure climbs; per-actor clocks make
        // single-move comparisons decay-timing-skewed, so only the
        // climb is asserted here (Thrusting_BringsTheDriverToClimax
        // proves the destination)
        var (thrust, _) = Act(engine, "thrust", "sam");
        Assert.True(Field(engine, "player", "pleasure") > 0.05);

        var (shift, _) = Act(engine, "shift", "sam");
        Assert.Contains("gather them back", shift.Message);
        Assert.Equal("behind", Embraces.Position(engine.ModuleRegistry,
            EmbraceBetween(engine, "player", "sam")!));

        // an undeclared position refuses to arrange
        var (spoon, _) = Act(engine, "spoon", "sam");
        Assert.Contains("do not bend", spoon.Message);

        var (done, _) = Act(engine, "withdraw", "sam");
        Assert.Contains("ease apart", done.Message);
        Assert.Null(EmbraceBetween(engine, "player", "sam"));
    }

    [Fact]
    public void JoinedSubActions_List_OnlyWhileEmbraced()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");

        // before joining: the sub-action isn't even offered — for
        // EITHER member of the future pair
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.Verb == "thrust");
        Assert.DoesNotContain(engine.ActionResolver.Resolve(sam),
            a => a.Verb == "thrust");

        JoinThem(engine);

        Assert.Contains(engine.ActionResolver.Resolve(player),
            a => a.Verb == "thrust" && a.TargetId == "sam");
        // mutuality: the partner has her own sub-actions too, aimed
        // back at the joiner
        Assert.Contains(engine.ActionResolver.Resolve(sam),
            a => a.Verb == "thrust" && a.TargetId == "player");
        Assert.Contains(engine.ActionResolver.Resolve(sam),
            a => a.Verb == "withdraw" && a.TargetId == "player");
    }

    [Fact]
    public void TurnBased_OtherAgentsActions_DoNotDrainYourPleasure()
    {
        var engine = NewFixture();
        Set(engine, "sam", "comfort", 60);
        var sam = engine.World.GetObject("sam");

        // interleaved kisses (player) and chatter (sam): sam's own say
        // advances only sam's clock by their duration; the player's kisses
        // must accumulate pleasure faster than sam's own talk decays it —
        // the cast size must not scale the world clock
        var say = TestWorlds.Find(engine, "sam", "say", "sam");
        var neck = "sam_neck";
        for (var i = 0; i < 3; i++)
        {
            Act(engine, "kiss", neck);
            engine.TurnManager.PerformAction(sam, say, "mm-hm");
        }
        Assert.True(Field(engine, "sam", "pleasure") > 0.05,
            $"pleasure should accumulate across kisses, was {Field(engine, "sam", "pleasure")}");
    }

    [Fact]
    public void Thrusting_BringsTheDriverToClimax()
    {
        var engine = NewFixture();
        JoinThem(engine);
        var player = engine.World.GetObject("player");

        for (var i = 0; i < 16 &&
                      !Conditions.Has(engine.World, engine.ModuleRegistry, player, "satisfied"); i++)
            Act(engine, "thrust", "sam");
        Assert.True(Conditions.Has(engine.World, engine.ModuleRegistry, player, "satisfied"));
    }

    [Fact]
    public void Withdraw_Unfinished_LeavesThemRestless()
    {
        var engine = NewFixture();
        JoinThem(engine);
        Set(engine, "sam", "arousal", 0.8); // aroused, not satisfied

        var (done, _) = Act(engine, "withdraw", "sam");
        Assert.Contains("not finished", done.Message);
        // frustration banks on its own from here
        Motives.Advance(engine, 120);
        Assert.True(Field(engine, "sam", "frustration") > 0.0);
    }

    [Fact]
    public void Hug_FleetingKind_AppliesAndDissolves()
    {
        var engine = NewFixture();
        Set(engine, "sam", "comfort", 60);

        var (result, reacted) = Act(engine, "hug", "sam");

        Assert.True(reacted); // welcomed at high comfort
        Assert.Contains("a moment whole", result.Message);
        Assert.True(Field(engine, "sam", "comfort") > 63.0); // impulses landed both sides
        Assert.True(Field(engine, "player", "comfort") > 48.0);
        // autoEnd: the hug is over with the action — no pair state remains
        Assert.Null(EmbraceBetween(engine, "player", "sam"));
    }

    [Fact]
    public void Hug_HesitantReaction_HalvesTheWarmth()
    {
        var engine = NewFixture(); // sam's comfort 52 < 50? no — 52 ≥ 50...

        // sam's default comfort is 52 (welcome threshold 50): set below it
        Set(engine, "sam", "comfort", 45);
        var (result, reacted) = Act(engine, "hug", "sam");

        Assert.True(reacted);
        Assert.Contains("a moment whole", result.Message); // still a hug —
        // a hesitant one: impulses halved (4 * 0.5 = 2)
        Assert.InRange(Field(engine, "sam", "comfort"), 46.0, 48.0);
    }

    [Fact]
    public void LapSit_AsymmetricKind_PersistsUntilDisengage()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");
        engine.TurnManager.PerformAction(sam, TestWorlds.Find(engine, "sam", "sit", "sofa"));

        // the lap is offered only while the partner is seated
        Assert.Contains(engine.ActionResolver.Resolve(player),
            a => a.Verb == "lap" && a.TargetId == "sam");

        var (result, _) = Act(engine, "lap", "sam");
        Assert.Contains("settle onto their lap", result.Message);
        var embrace = EmbraceBetween(engine, "player", "sam");
        Assert.NotNull(embrace);
        Assert.Equal("lap", Embraces.Kind(engine.ModuleRegistry, embrace!));

        // one embrace per agent: no second pairing while lapped
        Set(engine, "sam", "comfort", 60);
        var (busy, _) = Act(engine, "hug", "sam");
        Assert.Contains("already holding", busy.Message);

        // and it lasts until disengaged
        var (done, _) = Act(engine, "climbdown", "sam");
        Assert.Contains("with dignity", done.Message);
        Assert.Null(EmbraceBetween(engine, "player", "sam"));
    }

    [Fact]
    public void SameVerbAffordances_ExecuteTheirOwnData()
    {
        // three reposition affordances share the verb "shift": the
        // executed entry must carry its OWN data (position, prose) —
        // matched by label, the way plans and menus pick entries
        var engine = NewFixture();
        JoinThem(engine);
        var player = engine.World.GetObject("player");

        (string, string) ByLabel(string label)
        {
            var action = engine.ActionResolver.Resolve(player)
                .First(a => a.Verb == "shift" && a.Label == label);
            var result = engine.TurnManager.PerformAction(player, action);
            return (result.Message,
                Embraces.Position(engine.ModuleRegistry,
                    EmbraceBetween(engine, "player", "sam")!));
        }

        var (behindMsg, behind) = ByLabel("Turn Sam gently away from you");
        Assert.Contains("gather them back", behindMsg);
        Assert.Equal("behind", behind);
        // each same-verb affordance emits ITS OWN signal — the second
        // one's prose, not the first's (the entry index disambiguates)
        engine.SignalBus.Drain("sam");
        // the SECOND same-verb affordance: before the index fix this
        // executed the first one's data and nooped
        var (faceMsg, face) = ByLabel("Face Sam again");
        Assert.Contains("face them once more", faceMsg);
        Assert.Equal("face-to-face", face);
        Assert.Contains(engine.SignalBus.Drain("sam"),
            s => s.Text == "Riley turns to face you again.");
    }

    private static void Strip(GameEngine engine, params string[][] garmentSets)
    {
        foreach (var garments in garmentSets)
            foreach (var id in garments)
                engine.World.SetFieldOverride(id, "wearable", "worn", World.ToJson(false));
    }

    [Fact]
    public void ProbeTouch_UsesTheInstrument_AndCompatibilityRules()
    {
        var engine = NewFixture();
        Strip(engine,
            new[] { "g_sweater", "g_sam_trousers", "g_underwear" });
        Set(engine, "sam", "comfort", 60);
        var player = engine.World.GetObject("player");

        // probe affordances list only orifices that receive the tag —
        // breasts and thighs are surfaces, not openings, so "finger"
        // offers the sex alone
        var fingerLabels = engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb == "finger").Select(a => a.Label).ToList();
        Assert.Equal(["Finger Sam's sex"], fingerLabels);

        // a compatible probe resolves the actor's part and renders it
        var (result, _) = Act(engine, "finger", "sam_sex");
        Assert.Contains("fingers", result.Message);
        Assert.True(Field(engine, "sam", "pleasure") > 0.0);

        // an instrument under clothes stays there (its wear region is
        // the authoring knob — fingers and tongues have none)
        var (clothed, _) = Act(engine, "grind", "sam_sex");
        Assert.Contains("Your own clothes are in the way", clothed.Message);
        Strip(engine, new[] { "g_trousers", "g_shorts" });
        var (bare, _) = Act(engine, "grind", "sam_sex");
        Assert.Contains("You press", bare.Message);
    }

    [Fact]
    public void ProbeTouch_HeldToysProvideInstruments()
    {
        var engine = NewFixture();
        Strip(engine,
            new[] { "g_sweater", "g_sam_trousers", "g_underwear" });
        var player = engine.World.GetObject("player");

        // nothing on hand provides the probe
        var (none, _) = Act(engine, "tease", "sam_sex");
        Assert.Contains("nothing of the sort", none.Message);

        // a held toy carrying its own probe does
        engine.TurnManager.PerformAction(player, TestWorlds.Find(engine, "player", "take", "dildo"));
        var (with, _) = Act(engine, "tease", "sam_sex");
        Assert.Contains("smooth toy", with.Message);
    }

    [Fact]
    public void ThroughClothes_IsItsOwnAffordance_GatedToCoveredParts()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        List<string> Labels(string verb) =>
            engine.ActionResolver.Resolve(player)
                .Where(a => a.Verb == verb).Select(a => a.Label).ToList();

        // dressed: the through-clothes verb lists covered regions only
        var rub = Labels("rub");
        Assert.Contains("Rub Sam's chest through the fabric", rub);
        Assert.DoesNotContain(rub, l => l.Contains("neck")); // no region, never covered
        // the bare-skin family hides what's covered, shows the rest
        Assert.Contains(Labels("massage"), l => l.Contains("Sam's neck"));
        Assert.DoesNotContain(Labels("stroke"), l => l.Contains("Sam's chest"));

        // stripped: the through-clothes entries vanish
        Strip(engine, new[] { "g_sweater", "g_sam_trousers", "g_underwear" });
        Assert.Empty(Labels("rub"));
        Assert.Contains(Labels("stroke"), l => l.Contains("Sam's chest"));
    }

    [Fact]
    public void BusyParts_BlockConflictingTouches_WhileEmbraced()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");
        engine.TurnManager.PerformAction(player, TestWorlds.Find(engine, "player", "sit", "sofa"));
        engine.TurnManager.PerformAction(sam, TestWorlds.Find(engine, "sam", "sit", "sofa"));
        Set(engine, "sam", "comfort", 60);
        Strip(engine, new[] { "g_sweater", "g_sam_trousers", "g_underwear" });

        var (down, _) = Act(engine, "odown", "sam");
        Assert.Contains("devoted", down.Message);
        Assert.NotNull(EmbraceBetween(engine, "player", "sam"));

        // the giver's lips are busy (usesParts): any kiss is declined
        var (kiss, _) = Act(engine, "kiss", "sam_neck");
        Assert.Contains("otherwise spoken for", kiss.Message);
        // the receiver's sex is busy: fingering is declined
        var (finger, _) = Act(engine, "finger", "sam_sex");
        Assert.Contains("isn't free", finger.Message);
        // unoccupied parts stay available
        var (neck, _) = Act(engine, "massage", "sam_neck");
        Assert.Equal("You massage Sam's neck.", neck.Message);

        // and everything returns when the embrace ends
        var (up, _) = Act(engine, "riseup", "sam");
        Assert.Contains("raise your head", up.Message);
        Assert.Null(EmbraceBetween(engine, "player", "sam"));
        var (kissAgain, _) = Act(engine, "kiss", "sam_neck");
        Assert.Equal("You kiss Sam's neck.", kissAgain.Message);
    }

    [Fact]
    public void Undergarments_CoverFinely_ThighsStayBare()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        // sam in underwear only: the hips are covered, the thighs are not
        Strip(engine, new[] { "g_sweater", "g_sam_trousers" });
        List<string> Labels(string verb) =>
            engine.ActionResolver.Resolve(player).Where(a => a.Verb == verb)
                .Select(a => a.Label).ToList();

        var rub = Labels("rub"); // the through-clothes family
        Assert.Contains(rub, l => l.Contains("Sam's sex")); // hips covered by underwear
        Assert.DoesNotContain(rub, l => l.Contains("thighs")); // thighs are bare

        var stroke = Labels("stroke"); // the bare-skin family
        Assert.Contains("Stroke Sam's thighs", stroke); // bare
        Assert.DoesNotContain(stroke, l => l.Contains("sex")); // covered
    }

    [Fact]
    public void Outerwear_StacksOverUndergarments()
    {
        var engine = NewFixture();
        var sam = engine.World.GetObject("sam");

        // trousers go back on over the underwear: same regions, different
        // layers — no conflict, both worn
        engine.World.SetFieldOverride("g_sam_trousers", "wearable", "worn", World.ToJson(false));
        var result = engine.TurnManager.PerformAction(
            sam, TestWorlds.Find(engine, "sam", "wear", "g_sam_trousers"));
        Assert.True(result.Success, result.Message);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("g_sam_trousers"), "wearable", "worn"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("g_underwear"), "wearable", "worn"));
    }

    [Fact]
    public void RemovingAnotherAgentsGarment_ConsentGates_TheAttempt()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");

        // cold: the default answer is a caught hand, and the garment
        // STAYS ON — the refusal is honored, not narrated past
        var (refused, _) = Act(engine, "remove", "g_sweater");
        Assert.Contains("keeps it on", refused.Message);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("g_sweater"), "wearable", "worn"));

        // warm enough: she lets it happen, and it comes off (into the
        // remover's hands, like any pull)
        Set(engine, "sam", "comfort", 65);
        var (let, _) = Act(engine, "remove", "g_sweater");
        Assert.Contains("pull the sweater off", let.Message);
        Assert.False(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("g_sweater"), "wearable", "worn"));
        Assert.Equal("player", engine.World.GetObject("g_sweater").Parent);
    }

    [Fact]
    public void TargetsProbes_ActionsListInsertableParts_Only()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");

        // "suck" targets parts acting as the listed probes — the fingers
        // offer themselves, the neck (no probe) does not
        var labels = engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb == "suck").Select(a => a.Label).ToList();
        Assert.Equal(["Suck on Sam's fingers"], labels);

        // executing it lands impulses on the part's OWNER (the receiver)
        // and warms the giver
        var before = Field(engine, "sam", "comfort");
        var (result, reacted) = Act(engine, "suck", "sam_fingers");
        Assert.False(reacted); // no reaction declared on the fixture's suck
        Assert.Contains("mouth", result.Message);
        Assert.True(Field(engine, "sam", "comfort") > before);
        Assert.True(Field(engine, "player", "arousal") > 0.18);
    }

    [Fact]
    public void Pronouns_ResolveByBundle_OverridePerAgent_FallBackGeneric()
    {
        var engine = NewFixture();
        var modules = engine.ModuleRegistry;
        var sam = engine.World.GetObject("sam");
        var player = engine.World.GetObject("player");

        // bundle defaults
        Assert.Equal("she", Pronouns.Subject(modules, sam));
        Assert.Equal("her", Pronouns.Possessive(modules, sam));
        Assert.Equal("herself", Pronouns.Reflexive(modules, sam));
        Assert.Equal("he", Pronouns.Subject(modules, player));

        // a per-agent override wins over the bundle
        engine.World.SetFieldOverride("sam", "pronouns", "subject", World.ToJson("they"));
        Assert.Equal("they", Pronouns.Subject(modules, sam));
        Assert.Equal("her", Pronouns.Possessive(modules, sam)); // untouched form

        // agents without pronouns get the generic set
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        Assert.Equal("they", Pronouns.Subject(modules, engine.World.GetObject("carol")));
        Assert.Equal("them", Pronouns.Object(modules, engine.World.GetObject("carol")));
    }

    [Fact]
    public void PronounTags_RenderObserverRelatively_WithVerbAgreement()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");

        // third person: a bystander-style render names her forms
        var text = Pronouns.ReplaceReferent(
            "{agent.subject} takes {agent.possessive} chance; the win is {agent.possessivePronoun}.",
            "agent", sam, observer: player, engine.ModuleRegistry);
        Assert.Equal("she takes her chance; the win is hers.", text);

        // second person: the observer is the referent — and a subject
        // tag at sentence start de-conjugates its verb
        var own = Pronouns.ReplaceReferent(
            "{agent.subject} takes {agent.possessive} chance by {agent.reflexive}.",
            "agent", sam, observer: sam, engine.ModuleRegistry);
        Assert.Equal("you take your chance by yourself.", own);

        // a null referent's tags collapse to nothing
        var none = Pronouns.ReplaceReferent("{holder.subject} nods.", "holder", null, null, engine.ModuleRegistry);
        Assert.Equal(" nods.", none);
    }

    [Fact]
    public void Signals_RenderPronounTags_PerObserver()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");
        engine.SignalBus.Emit(sam, null,
            [new AEngine.Core.Signals.SignalSpec
            {
                Sense = AEngine.Core.Signals.SignalSense.Visual,
                Priority = 5,
                Text = "{agent.subject} tucks {agent.possessive} hair back.",
            }]);
        // a bystander reads her forms
        Assert.Contains(engine.SignalBus.Drain("player"), s => s.Text == "she tucks her hair back.");

        // and the referent reads the same signal about THEMSELF in the
        // second person — via the {target} tags, since actors never
        // observe their own emissions
        engine.SignalBus.Emit(player, sam,
            [new AEngine.Core.Signals.SignalSpec
            {
                Sense = AEngine.Core.Signals.SignalSense.Visual,
                Priority = 5,
                Text = "{target.subject} tucks {target.possessive} hair back.",
            }]);
        Assert.Contains(engine.SignalBus.Drain("sam"), s => s.Text == "you tuck your hair back.");
    }

    [Fact]
    public void SelfOnly_Affordances_ListOwnParts_Exclusively()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");

        // dressed: own intimate parts stay hidden like anyone else's
        Assert.Empty(engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb == "masturbate"));

        // undressed: the verb offers the actor's OWN parts only — no
        // "Masturbate Sam's sex", which would be a different action
        Strip(engine, new[] { "g_trousers", "g_shorts" });
        var labels = engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb == "masturbate").Select(a => a.Label).ToList();
        Assert.Equal(["Masturbate your own sex"], labels);

        // executing it lands the impulses on the self (receiver and
        // giver are the same agent) and a bystander sees the signal
        var (result, reacted) = Act(engine, "masturbate", "player_sex");
        Assert.False(reacted); // no reaction to your own touch
        Assert.Equal("You take your own sex in hand, breath going with it.", result.Message);
        Assert.True(Field(engine, "player", "pleasure") > 0.0);
        Assert.Contains(engine.SignalBus.Drain("sam"),
            s => s.Text == "Riley takes his sex in hand, unhurried.");
    }

    private static List<WorldObject> SemenAt(GameEngine engine, string parentId) =>
        engine.World.ChildrenOf(parentId)
            .Where(o => o.Id == "tpl_semen" || o.Id.StartsWith("tpl_semen_", StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void Emission_LandsBesideTheEmitter_WhenUnengaged()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");

        Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            player, new Dictionary<string, double> { ["pleasure"] = 1.0 });

        // no embrace, no condom: the emission is a SIBLING — the room
        // floor (or whatever support the emitter is on)
        var landed = SemenAt(engine, "room_a");
        Assert.Single(landed);
        Assert.Equal("semen", landed[0].Name);
    }

    [Fact]
    public void Emission_LandsInTheEngagedOrifice_WhileJoined()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        JoinThem(engine); // embraces occupy the player's sex and sam's sex

        Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            player, new Dictionary<string, double> { ["pleasure"] = 1.0 });

        // the player's probe part (sex, probe penis) is occupied, and
        // sam's occupied part receives it — the emission lands there
        Assert.Single(SemenAt(engine, "sam_sex"));
        Assert.Empty(SemenAt(engine, "room_a"));
        // and examine of the part renders the deposit
        var examined = engine.TurnManager.Execute(player, "examine", "sam_sex");
        Assert.Contains("semen", examined.Message);
    }

    [Fact]
    public void Emission_IsCollectedByAWornCondom_WhichFills()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        engine.World.MoveObject("condom", "player");
        engine.TurnManager.Execute(player, "wear", "condom");
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("condom"), "wearable", "worn"));

        Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            player, new Dictionary<string, double> { ["pleasure"] = 1.0 });

        // the condom collected everything: filled, renamed, nothing
        // spilled anywhere
        Assert.Single(SemenAt(engine, "condom"));
        Assert.Empty(SemenAt(engine, "room_a"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("condom"), "condom", "filled"));
        Assert.Equal("filled condom", engine.World.GetObject("condom").Name);
    }

    [Fact]
    public void Emission_AfterCondomRemoval_LandsOutsideAgain()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var condom = engine.World.GetObject("condom");
        engine.World.MoveObject("condom", "player");
        engine.TurnManager.Execute(player, "wear", "condom");
        Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            player, new Dictionary<string, double> { ["pleasure"] = 1.0 });
        Assert.Single(SemenAt(engine, "condom"));

        // take it off: the next emission has nowhere to go but beside
        engine.TurnManager.Execute(player, "remove", "condom");
        Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            player, new Dictionary<string, double> { ["pleasure"] = 1.0 });
        Assert.Single(SemenAt(engine, "room_a"));
        // and they accumulate — no cap
        Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            player, new Dictionary<string, double> { ["pleasure"] = 1.0 });
        Assert.Equal(2, SemenAt(engine, "room_a").Count);
    }

    [Fact]
    public void Emission_LandsInTheEngagedOrifice_WhoeverInitiated()
    {
        // the embrace's occupied lists are entry-order roles — the
        // emission must find the engaged orifice regardless of which
        // member initiated the embrace
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");
        engine.TurnManager.PerformAction(player, TestWorlds.Find(engine, "player", "sit", "sofa"));
        engine.TurnManager.PerformAction(sam, TestWorlds.Find(engine, "sam", "sit", "sofa"));
        Set(engine, "player", "comfort", 70); // the join gates check the TARGET
        Set(engine, "player", "arousal", 0.6);
        Set(engine, "sam", "comfort", 70);
        Set(engine, "sam", "arousal", 0.6);
        Strip(engine,
            new[] { "g_sweater", "g_sam_trousers", "g_underwear", "g_trousers", "g_shorts" });

        // SAM initiates — the join telegraphs and parks against the
        // player, resolving via the effective default at the deadline
        engine.TurnManager.PerformAction(sam, TestWorlds.Find(engine, "sam", "join", "player"));
        for (var i = 0; i < 6 && EmbraceBetween(engine, "player", "sam") is null; i++)
            engine.TurnManager.PerformAction(
                player, TestWorlds.Find(engine, "player", "wait"));
        Assert.NotNull(EmbraceBetween(engine, "player", "sam"));

        Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            player, new Dictionary<string, double> { ["pleasure"] = 1.0 });

        Assert.Single(SemenAt(engine, "sam_sex"));
        Assert.Empty(SemenAt(engine, "room_a"));
    }

    [Fact]
    public void Emission_InAnEmbrace_WithoutTheProbeEngaged_LandsBeside()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");
        // a lap embrace: occupies nothing — hugging doesn't engage the
        // probe, so there is no orifice to land in
        engine.TurnManager.PerformAction(sam, TestWorlds.Find(engine, "sam", "sit", "sofa"));
        Act(engine, "lap", "sam");
        Assert.NotNull(EmbraceBetween(engine, "player", "sam"));

        Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            player, new Dictionary<string, double> { ["pleasure"] = 1.0 });

        Assert.Empty(SemenAt(engine, "sam_sex"));
        Assert.Single(SemenAt(engine, "room_a"));
    }

    [Fact]
    public void LyingDown_RendersTheLieMessage()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        Strip(engine, new[] { "g_trousers", "g_shorts" }); // irrelevant, hygiene

        var result = engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "lie", "sofa"));

        Assert.Equal("You lie down on the sofa.", result.Message);
        Assert.Equal(Postures.Lying, Postures.Of(engine.World, engine.ModuleRegistry, player));
    }

    [Fact]
    public void SitAndLie_TransitionDirectly_OnTheSameSupport()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");

        engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "sit", "sofa"));
        Assert.Equal(Postures.Sitting, Postures.Of(engine.World, engine.ModuleRegistry, player));

        // sitting -> lying, without standing up first
        var lie = engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "lie", "sofa"));
        Assert.Equal("You stretch out on the sofa.", lie.Message);
        Assert.Equal(Postures.Lying, Postures.Of(engine.World, engine.ModuleRegistry, player));

        // and back up
        var sit = engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "sit", "sofa"));
        Assert.Equal("You sit up on the sofa.", sit.Message);
        Assert.Equal(Postures.Sitting, Postures.Of(engine.World, engine.ModuleRegistry, player));

        // a different support still requires standing
        engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "lie", "sofa"));
        var far = engine.TurnManager.Execute(player, "sit", "sofa");
        Assert.Equal(ActionOutcome.Success, far.Outcome); // same support still fine
        var stand = engine.TurnManager.Execute(player, "stand", "sofa");
        Assert.Contains("get up", stand.Message);
    }

    [Fact]
    public void Emission_DuringSucking_LandsInTheActorSOrifice()
    {
        var engine = NewFixture();
        var sam = engine.World.GetObject("sam");
        Strip(engine, new[] { "g_trousers", "g_shorts" });
        Set(engine, "player", "pleasure", 0.95);

        // sam takes the player's probe part into her mouth (usesParts
        // lips); his climax emits INTO the engaged orifice
        var action = engine.ActionResolver.Resolve(sam)
            .First(a => a.Verb == "suck" && a.TargetId == "player_sex");
        var result = engine.TurnManager.PerformAction(sam, action);
        Assert.True(result.Success, result.Message);
        Assert.Single(SemenAt(engine, "sam_lips"));
        Assert.Empty(SemenAt(engine, "room_a"));
    }

    [Fact]
    public void FilledCondom_CoversTheHips_AndCannotBeReworn()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        engine.World.MoveObject("condom", "player");
        Assert.True(engine.TurnManager.Execute(player, "wear", "condom").Success);

        Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            player, new Dictionary<string, double> { ["pleasure"] = 1.0 });
        Assert.Single(SemenAt(engine, "condom"));

        // filled: it now covers the hips — the player's own sex (region
        // hips) is covered, so the private listing hides it
        Strip(engine, new[] { "g_trousers", "g_shorts" });
        Assert.Empty(engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb == "masturbate"));

        // and once taken off, it can't go back on (the wear gate is
        // execution-time, so the attempt must go through PerformAction)
        Assert.Contains("You take off", engine.TurnManager.Execute(player, "remove", "condom").Message);
        var reworn = engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "wear", "condom"));
        Assert.False(reworn.Success);
        Assert.Contains("spent", reworn.Message);
    }

    [Fact]
    public void BodyParts_AreExaminable_WithDescriptionAndDeposits()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        engine.World.GetObject("sam_sex").Description = "Quiet and pink and currently interested.";

        var labels = engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb == "examine").Select(a => a.Label).ToList();
        // non-intimate parts always list; intimate ones only when bare
        Assert.Contains("Examine Sam's lips", labels);
        Assert.Contains("Examine your own fingers", labels);
        Assert.DoesNotContain(labels, l => l.Contains("sex")); // dressed
        Strip(engine, new[] { "g_sweater", "g_sam_trousers", "g_underwear" });
        labels = engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb == "examine").Select(a => a.Label).ToList();
        Assert.Contains("Examine Sam's sex", labels);

        // the examine render: description plus deposits
        engine.World.CloneTree("tpl_semen", "sam_sex", "tpl_semen_e");
        var examined = engine.TurnManager.Execute(player, "examine", "sam_sex");
        Assert.Contains("Quiet and pink", examined.Message);
        Assert.Contains("semen", examined.Message);
    }

    [Fact]
    public void Cleanup_WithATissue_RemovesDeposits_AndSpendsTheTissue()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        Strip(engine, new[] { "g_trousers", "g_shorts" });
        // a deposit on the actor's own part (the mess the tissue is for)
        engine.World.CloneTree("tpl_semen", "player_sex", "tpl_semen_m");

        // no tissue held: refuse politely
        var (noTool, _) = Act(engine, "wipe", "player_sex");
        Assert.Contains("nothing to wipe", noTool.Message);

        // with a tissue: the deposit goes, the tissue is spent
        engine.World.MoveObject("tissue1", "player");
        var (wipe, _) = Act(engine, "wipe", "player_sex");
        Assert.Contains("wipe your sex clean", wipe.Message);
        Assert.Empty(SemenAt(engine, "player_sex"));
        var tissue = engine.World.GetObject("tissue1");
        Assert.Equal("soiled tissue", tissue.Name);
        Assert.True(engine.ModuleRegistry.ResolveBool(tissue, "tissue", "spent"));

        // a spent tissue can't clean again — and can be thrown away
        engine.World.CloneTree("tpl_semen", "player_sex", "tpl_semen_m2");
        var (spent, _) = Act(engine, "wipe", "player_sex");
        Assert.Contains("nothing to wipe", spent.Message);
        var tossed = engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "discard", "tissue1"));
        Assert.True(tossed.Success);
        Assert.False(engine.World.HasObject("tissue1"));
    }

    [Fact]
    public void Cleanup_Listing_OnlyOffersMessyParts()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");

        // clean lists nothing when everything is clean
        Assert.Empty(engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb is "wipe" or "swallow"));

        // a deposit in sam's lips: "Wipe Sam's lips" for the player,
        // "Swallow" for sam alone (selfOnly + the mouth gate)
        var clone = engine.World.CloneTree("tpl_semen", "sam_lips", "tpl_semen_x");
        var verbs = engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb == "wipe").Select(a => a.Label).ToList();
        Assert.Equal(["Wipe Sam's lips"], verbs);
        var samSwallow = engine.ActionResolver.Resolve(sam)
            .Where(a => a.Verb == "swallow").Select(a => a.Label).ToList();
        Assert.Equal(["Swallow the semen"], samSwallow);
        // the player can't swallow (the deposit isn't on their mouth)
        Assert.Empty(engine.ActionResolver.Resolve(player)
            .Where(a => a.Verb == "swallow"));

        // and swallowing removes it
        var action = engine.ActionResolver.Resolve(sam)
            .First(a => a.Verb == "swallow");
        var result = engine.TurnManager.PerformAction(sam, action);
        Assert.Contains("swallow it down", result.Message);
        Assert.Empty(SemenAt(engine, "sam_lips"));
    }

    [Fact]
    public void Shower_WashesEveryDepositOffTheBody()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        var sam = engine.World.GetObject("sam");
        engine.World.CloneTree("tpl_semen", "player_sex", "tpl_semen_a");
        engine.World.CloneTree("tpl_semen", "sam_lips", "tpl_semen_b");
        engine.World.CloneTree("tpl_semen", "room_a", "tpl_semen_c"); // not on a body

        var result = engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "shower", "shower"));

        Assert.Contains("hot water", result.Message);
        Assert.Empty(SemenAt(engine, "player_sex"));
        // only the actor's own body is rinsed
        Assert.Single(SemenAt(engine, "sam_lips"));
        Assert.Single(SemenAt(engine, "room_a"));
    }

    [Fact]
    public void Leaving_AppendsEndingSuffixesForActorConditions()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");

        var plain = engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "leave", "exitdoor"));
        Assert.True(plain.EndsGame);
        Assert.DoesNotContain("wave until the corner", plain.Message);

        var engine2 = NewFixture();
        Conditions.Attach(engine2.World, engine2.ModuleRegistry,
            engine2.World.GetObject("player"), "cond_satisfied");
        var sweet = engine2.TurnManager.PerformAction(
            engine2.World.GetObject("player"), TestWorlds.Find(engine2, "player", "leave", "exitdoor"));
        Assert.Contains("wave until the corner", sweet.Message);
    }
}
