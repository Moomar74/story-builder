using System;
using System.Collections.Generic;
using System.Drawing;

// ============================================================
//  Story Data Model
// ============================================================
public enum ChallengeType { PLACE, REMOVE, ROTATE, MOVE }

public class StoryCharacter
{
    public string Name;
    public string Emoji;
    public string ImageFile;   // e.g. "char_lion.png"
    public Color Color;
    public StoryCharacter(string name, string emoji, string img, Color color)
    { Name = name; Emoji = emoji; ImageFile = img; Color = color; }
}

public class Dialogue
{
    public int CharIndex;
    public string Text;
    public Dialogue(int ch, string txt) { CharIndex = ch; Text = txt; }
}

public class Challenge
{
    public string Instruction;
    public ChallengeType Type;
    public int RequiredMarkerId;
    public string SuccessMessage;
    public float RotateThreshold;
    public string ObjectImage;     // e.g. "obj_rock.png"
    public Challenge(string inst, ChallengeType t, int mkr, string success, string objImg = null, float rotThresh = 0.78f)
    {
        Instruction = inst; Type = t; RequiredMarkerId = mkr;
        SuccessMessage = success; ObjectImage = objImg; RotateThreshold = rotThresh;
    }
}

public class StoryScene
{
    public string Title;
    public string BackgroundImage;  // e.g. "bg_forest.png"
    public Color BgColor1, BgColor2; // fallback
    public List<Dialogue> Dialogues;
    public Challenge Challenge;

    public StoryScene(string title, string bgImg, Color c1, Color c2, List<Dialogue> dlg, Challenge ch)
    { Title = title; BackgroundImage = bgImg; BgColor1 = c1; BgColor2 = c2; Dialogues = dlg; Challenge = ch; }
}

public class Story
{
    public string Title;
    public string Emoji;
    public string Description;
    public Color CardColor;
    public StoryCharacter[] Characters;
    public StoryScene[] Scenes;
}

// ============================================================
//  All 4 Stories
// ============================================================
public static class StoryDatabase
{
    public static Story[] AllStories = new Story[]
    {
        // ---- Story 1: The Lion and the Lost Crown ----
        new Story
        {
            Title = "The Lion and\nthe Lost Crown",
            Emoji = "\U0001F981",
            Description = "Help the Lion find his stolen crown!",
            CardColor = Color.FromArgb(210, 132, 26),
            Characters = new StoryCharacter[]
            {
                new StoryCharacter("Lion",  "\U0001F981", "char_lion.png",  Color.FromArgb(210,132,26)),
                new StoryCharacter("Fox",   "\U0001F98A", "char_fox.png",   Color.FromArgb(232,101,26)),
                new StoryCharacter("Eagle", "\U0001F985", "char_eagle.png", Color.FromArgb(100,160,220)),
            },
            Scenes = new StoryScene[]
            {
                new StoryScene("The Stolen Crown", "bg_forest.png",
                    Color.ForestGreen, Color.DarkGreen,
                    new List<Dialogue> {
                        new Dialogue(0, "Oh no! My golden crown has been stolen!"),
                        new Dialogue(1, "Don't worry Lion! I found a map that shows where it went."),
                        new Dialogue(0, "Quick! Let me see the map!"),
                    },
                    new Challenge("Place Marker 5 to pick up the map!", ChallengeType.PLACE, 5,
                        "You got the map! Let's follow it!", "obj_map.png")),

                new StoryScene("The Blocked Path", "bg_forest.png",
                    Color.FromArgb(34,120,50), Color.FromArgb(20,80,30),
                    new List<Dialogue> {
                        new Dialogue(0, "The map says we go through the forest..."),
                        new Dialogue(1, "Oh no! A big rock is blocking our way!"),
                        new Dialogue(0, "We need to move it! Can you help?"),
                    },
                    new Challenge("Place Marker 6 on the rock, then REMOVE it to push it away!",
                        ChallengeType.REMOVE, 6, "The rock is gone! The path is clear!", "obj_rock.png")),

                new StoryScene("The Secret Cave", "bg_cave.png",
                    Color.FromArgb(60,60,80), Color.FromArgb(30,30,50),
                    new List<Dialogue> {
                        new Dialogue(2, "I flew ahead and found the cave! The crown is inside!"),
                        new Dialogue(0, "But the door is locked..."),
                        new Dialogue(2, "Try rotating the stone handle!"),
                    },
                    new Challenge("Rotate Marker 7 to open the cave door!",
                        ChallengeType.ROTATE, 7, "The cave is open! I can see something shining!")),

                new StoryScene("Crown Found!", "bg_cave.png",
                    Color.Gold, Color.Orange,
                    new List<Dialogue> {
                        new Dialogue(0, "There it is! My beautiful crown!"),
                        new Dialogue(1, "Pick it up, Your Majesty!"),
                        new Dialogue(0, "Thank you friends! You saved the kingdom!"),
                    },
                    new Challenge("Place Marker 8 to pick up the crown!",
                        ChallengeType.PLACE, 8, "The crown is back! Long live the King!", "obj_crown.png")),
            }
        },

        // ---- Story 2: The Fox and the Magic Bridge ----
        new Story
        {
            Title = "The Fox and\nthe Magic Bridge",
            Emoji = "\U0001F98A",
            Description = "Help the Fox cross the river!",
            CardColor = Color.FromArgb(232, 101, 26),
            Characters = new StoryCharacter[]
            {
                new StoryCharacter("Fox",    "\U0001F98A", "char_fox.png",   Color.FromArgb(232,101,26)),
                new StoryCharacter("Wolf",   "\U0001F43A", "char_wolf.png",  Color.FromArgb(120,120,142)),
                new StoryCharacter("Eagle",  "\U0001F985", "char_eagle.png", Color.FromArgb(200,180,160)),
            },
            Scenes = new StoryScene[]
            {
                new StoryScene("Friend Needs Help", "bg_river.png",
                    Color.LightSkyBlue, Color.DeepSkyBlue,
                    new List<Dialogue> {
                        new Dialogue(2, "Fox! Please help me! I need to get across the river!"),
                        new Dialogue(0, "Don't worry! I'll find planks for the bridge."),
                        new Dialogue(0, "Let me look around for wood."),
                    },
                    new Challenge("Place Marker 5 to collect wooden planks!",
                        ChallengeType.PLACE, 5, "Great! Now we have planks for the bridge!")),

                new StoryScene("The Wolf Guard", "bg_river.png",
                    Color.FromArgb(80,100,80), Color.FromArgb(50,70,50),
                    new List<Dialogue> {
                        new Dialogue(1, "Stop! Nobody crosses MY bridge!"),
                        new Dialogue(0, "Please Mr. Wolf, my friend needs help!"),
                        new Dialogue(1, "Hmm... bring me a fish and I'll let you pass."),
                    },
                    new Challenge("Place Marker 6 to give the Wolf a fish!",
                        ChallengeType.PLACE, 6, "The Wolf is happy! You may pass!")),

                new StoryScene("The Dark Path", "bg_cave.png",
                    Color.FromArgb(20,20,40), Color.FromArgb(10,10,30),
                    new List<Dialogue> {
                        new Dialogue(0, "It's so dark here... I can't see anything!"),
                        new Dialogue(0, "Wait, I found a lantern! But it needs to be lit."),
                    },
                    new Challenge("Rotate Marker 7 to light the lantern!",
                        ChallengeType.ROTATE, 7, "Light! Now I can see the path!")),

                new StoryScene("Bridge Crossed!", "bg_river.png",
                    Color.LightGreen, Color.MediumSeaGreen,
                    new List<Dialogue> {
                        new Dialogue(0, "We made it across the bridge!"),
                        new Dialogue(2, "Thank you so much, Fox! You're a true friend!"),
                        new Dialogue(0, "That's what friends are for!"),
                    },
                    new Challenge("Place Marker 8 to celebrate together!",
                        ChallengeType.PLACE, 8, "Friends forever! Hooray!")),
            }
        },

        // ---- Story 3: The Eagle's Sky Adventure ----
        new Story
        {
            Title = "The Eagle's\nSky Adventure",
            Emoji = "\U0001F985",
            Description = "Help the Eagle on a sky journey!",
            CardColor = Color.FromArgb(100, 160, 220),
            Characters = new StoryCharacter[]
            {
                new StoryCharacter("Eagle", "\U0001F985", "char_eagle.png", Color.FromArgb(100,160,220)),
                new StoryCharacter("Fox",   "\U0001F98A", "char_fox.png",   Color.FromArgb(140,100,60)),
                new StoryCharacter("Lion",  "\U0001F981", "char_lion.png",  Color.FromArgb(255,100,200)),
            },
            Scenes = new StoryScene[]
            {
                new StoryScene("Time to Fly!", "bg_sky.png",
                    Color.LightSkyBlue, Color.CornflowerBlue,
                    new List<Dialogue> {
                        new Dialogue(1, "Eagle! We need you to fly high and find the lost star!"),
                        new Dialogue(0, "I'm ready! Let me spread my wings!"),
                        new Dialogue(0, "Here I go, up into the sky!"),
                    },
                    new Challenge("Place Marker 5 so Eagle can take flight!",
                        ChallengeType.PLACE, 5, "Eagle is flying! Up, up, and away!")),

                new StoryScene("Strong Winds", "bg_sky.png",
                    Color.SteelBlue, Color.DarkSlateBlue,
                    new List<Dialogue> {
                        new Dialogue(0, "The wind is too strong! I need a shield!"),
                        new Dialogue(2, "Take this magic feather, it will protect you!"),
                    },
                    new Challenge("Place Marker 6 to pick up the shield feather!",
                        ChallengeType.PLACE, 6, "The feather shields you from the wind!")),

                new StoryScene("Cloudy Maze", "bg_sky.png",
                    Color.LightGray, Color.DimGray,
                    new List<Dialogue> {
                        new Dialogue(0, "Thick clouds are blocking my way!"),
                        new Dialogue(0, "I'll use my strong wings to push them away!"),
                    },
                    new Challenge("Rotate Marker 7 to push the clouds away!",
                        ChallengeType.ROTATE, 7, "The clouds are clearing! I can see the star!")),

                new StoryScene("Star Found!", "bg_sky.png",
                    Color.Gold, Color.LightPink,
                    new List<Dialogue> {
                        new Dialogue(0, "There it is! The lost star!"),
                        new Dialogue(1, "Bring it back to us!"),
                        new Dialogue(2, "Well done Eagle! You're a true hero!"),
                    },
                    new Challenge("Place Marker 8 to catch the star!",
                        ChallengeType.PLACE, 8, "The star is back! The sky is bright again!")),
            }
        },

        // ---- Story 4: The Wolf and the Hidden Treasure ----
        new Story
        {
            Title = "The Wolf and\nthe Hidden Treasure",
            Emoji = "\U0001F43A",
            Description = "Help the Wolf find the ancient treasure!",
            CardColor = Color.FromArgb(120, 120, 142),
            Characters = new StoryCharacter[]
            {
                new StoryCharacter("Wolf",  "\U0001F43A", "char_wolf.png",  Color.FromArgb(120,120,142)),
                new StoryCharacter("Lion",  "\U0001F981", "char_lion.png",  Color.FromArgb(139,90,43)),
                new StoryCharacter("Fox",   "\U0001F98A", "char_fox.png",   Color.FromArgb(180,140,80)),
            },
            Scenes = new StoryScene[]
            {
                new StoryScene("The Old Map", "bg_forest.png",
                    Color.BurlyWood, Color.SaddleBrown,
                    new List<Dialogue> {
                        new Dialogue(1, "Wolf! Look what I found! An old treasure map!"),
                        new Dialogue(0, "Wow! Let's unfold it and see where it leads!"),
                    },
                    new Challenge("Rotate Marker 5 to unfold the treasure map!",
                        ChallengeType.ROTATE, 5, "The map is open! X marks the spot!", "obj_map.png")),

                new StoryScene("The Fallen Tree", "bg_forest.png",
                    Color.DarkGreen, Color.FromArgb(0,60,0),
                    new List<Dialogue> {
                        new Dialogue(0, "A huge tree fell and blocked our path!"),
                        new Dialogue(1, "We need to move it together!"),
                    },
                    new Challenge("Place Marker 6 on the tree, then REMOVE it!",
                        ChallengeType.REMOVE, 6, "The tree is moved! Great teamwork!", "obj_rock.png")),

                new StoryScene("The Hungry Fox", "bg_forest.png",
                    Color.FromArgb(60,120,60), Color.FromArgb(30,80,30),
                    new List<Dialogue> {
                        new Dialogue(2, "I know where the treasure is! But I'm so hungry..."),
                        new Dialogue(0, "We'll find you some berries!"),
                        new Dialogue(2, "Bring me berries and I'll show you the way!"),
                    },
                    new Challenge("Place Marker 7 to pick berries for Fox!",
                        ChallengeType.PLACE, 7, "Yummy berries! Fox will guide you now!")),

                new StoryScene("Treasure Found!", "bg_cave.png",
                    Color.Gold, Color.DarkGoldenrod,
                    new List<Dialogue> {
                        new Dialogue(2, "Here it is! The ancient treasure chest!"),
                        new Dialogue(0, "Quick! Let's open it!"),
                        new Dialogue(1, "I can't believe it! We're rich!"),
                    },
                    new Challenge("Rotate Marker 8 to open the treasure chest!",
                        ChallengeType.ROTATE, 8, "The treasure is yours! What an adventure!", "obj_chest.png")),
            }
        },
    };
}
