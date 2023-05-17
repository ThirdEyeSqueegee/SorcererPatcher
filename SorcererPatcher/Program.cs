using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Noggog;

namespace SorcererPatcher
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "SorcererRecipeGenerator.esp")
                .Run(args);
        }

        private static readonly ModKey Sorcerer = ModKey.FromNameAndExtension("Sorcerer.esp");

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            if (!state.LoadOrder.ContainsKey(Sorcerer))
                throw new Exception("ERROR: Sorcerer.esp not found in load order");

            var scrollWorkbenchKywd = state.LinkCache.Resolve<IKeywordGetter>("MAG_TableScrollEnchanter").ToNullableLink();
            var staffWorkbenchKywd = state.LinkCache.Resolve<IKeywordGetter>("MAG_TableStaffEnchanter").ToNullableLink();
            var vanillaStaffWorkbenchKywd = state.LinkCache.Resolve<IKeywordGetter>("DLC2StaffEnchanter").ToNullableLink();
            var soulGemCommon = state.LinkCache.Resolve<ISoulGemGetter>("SoulGemCommonFilled").ToLink();
            var soulGemGreater = state.LinkCache.Resolve<ISoulGemGetter>("SoulGemGreaterFilled").ToLink();
            var soulGemGrand = state.LinkCache.Resolve<ISoulGemGetter>("SoulGemGrandFilled").ToLink();
            var pickUpSound = state.LinkCache.Resolve<ISoundDescriptorGetter>("ITMNoteUp").ToNullableLink();
            var inventoryArt = state.LinkCache.Resolve<IStaticGetter>("MAG_ResearchItemScroll").ToNullableLink();
            var ink = state.LinkCache.Resolve<IMiscItemGetter>("MAG_EnchantedInk");
            var paper = state.LinkCache.Resolve<IMiscItemGetter>("MAG_ScrollPaper");

            foreach (var scroll in state.LoadOrder.PriorityOrder.Scroll().WinningContextOverrides())
            {
                var sName = scroll.Record.Name!.ToString()!;
                var edid = scroll.Record.EditorID!;

                if (edid.Contains("MAG_") || sName.Contains("Shalidor") || sName.Contains("J'zargo") || sName.Contains("Spider")) continue;

                Console.WriteLine($"Processing scroll: {scroll.Record.Name}");

                // Determine recipe based on minimum skill level of costliest magic effect
                var max = 0.0f;
                uint costliestEffectLevel = 0;

                // Find minimum skill level of magic effect with the highest base cost
                foreach (var effect in scroll.Record.Effects)
                {
                    var record = state.LinkCache.Resolve<IMagicEffectGetter>(effect.BaseEffect.FormKey);
                    if (!(record.BaseCost > max)) continue;
                    max = record.BaseCost;
                    costliestEffectLevel = record.MinimumSkillLevel;
                }

                var patched = state.PatchMod.Scrolls.GetOrAddAsOverride(scroll.Record);
                var prevValue = patched.Value;
                patched.Value = costliestEffectLevel switch
                {
                    < 25 => 15,
                    >= 25 and < 50 => 30,
                    >= 50 and < 75 => 55,
                    >= 75 and < 100 => 100,
                    >= 100 => 160
                };

                // Removed unchanged records
                if (patched.Value == prevValue)
                    state.PatchMod.Remove(scroll.Record);

                var recipes = new List<(int, int, ushort)> // (scroll paper, enchanted ink, # of scrolls created)
                {
                    (2, 8, 2), // Master
                    (2, 5, 2), // Expert
                    (3, 4, 3), // Adept
                    (4, 3, 4), // Apprentice
                    (5, 2, 5) // Novice
                };

                var recipeToUse = costliestEffectLevel switch
                {
                    < 25 => recipes[4],
                    >= 25 and < 50 => recipes[3],
                    >= 50 and < 75 => recipes[2],
                    >= 75 and < 100 => recipes[1],
                    >= 100 => recipes[0]
                };

                var book = state.PatchMod.Books.AddNew();
                var perk = state.PatchMod.Perks.AddNew();
                var recipe = state.PatchMod.ConstructibleObjects.AddNew();
                var breakdownRecipe = state.PatchMod.ConstructibleObjects.AddNew();
                var name = scroll.Record.Name!.ToString()!.Replace("Scroll of the ", "").Replace("Scroll of ", "");
                var nameStripped = name.Replace(" ", "");

                // Book logic
                book.EditorID = "MAG_ResearchNotes" + nameStripped;
                book.Name = "Research Notes: " + name;
                book.Weight = 0;
                book.Value = costliestEffectLevel switch
                {
                    < 25 => 100,
                    >= 25 and < 50 => 200,
                    >= 50 and < 75 => 300,
                    >= 75 and < 100 => 500,
                    >= 100 => 800
                };
                book.PickUpSound = pickUpSound;
                book.BookText = book.Name;
                book.Description = scroll.Record.Name!.ToString()!.Contains("of the") switch
                {
                    true => $"Allows you to craft Scrolls of the " + name + ".",
                    false => $"Allows you to craft Scrolls of " + name + "."
                };
                book.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>>()
                {
                    state.LinkCache.Resolve<IKeywordGetter>("MAG_ScrollResearchNotes")
                };
                book.InventoryArt = inventoryArt;
                book.Model = new Model
                {
                    File = "Clutter\\Common\\Scroll05.nif",
                };
                ScriptProperty attachedBook = new ScriptObjectProperty
                {
                    Name = "AttachedBook",
                    Object = book.ToNullableLink()
                };
                ScriptProperty craftingPerk = new ScriptObjectProperty
                {
                    Name = "CraftingPerk",
                    Object = perk.ToNullableLink()
                };
                book.VirtualMachineAdapter = new VirtualMachineAdapter
                {
                    Scripts = new ExtendedList<ScriptEntry>
                    {
                        new()
                        {
                            Name = "MAG_ResearchItem_Script",
                            Properties = new ExtendedList<ScriptProperty>
                            {
                                attachedBook, craftingPerk
                            }
                        }
                    }
                };
                Console.WriteLine($"    Generated research notes");

                // Perk logic
                perk.EditorID = "MAG_ResearchPerk" + nameStripped;
                perk.Name = name + " Research Perk";
                perk.Playable = true;
                perk.Hidden = true;
                perk.Level = 0;
                perk.NumRanks = 1;
                Console.WriteLine($"    Generated perk");

                // Recipe logic
                recipe.EditorID = "MAG_RecipeScroll" + nameStripped;
                recipe.CreatedObject = scroll.Record.ToNullableLink();
                recipe.CreatedObjectCount = recipeToUse.Item3;
                recipe.WorkbenchKeyword = scrollWorkbenchKywd;
                var hasPerkCondData = new HasPerkConditionData();
                hasPerkCondData.Perk.Link.SetTo(perk);
                Condition hasPerk = new ConditionFloat
                {
                    CompareOperator = CompareOperator.EqualTo,
                    ComparisonValue = 1.0f,
                    Data = hasPerkCondData
                };
                recipe.Items = new ExtendedList<ContainerEntry>
                {
                    new()
                    {
                        Item = new ContainerItem
                        {
                            Item = ink.ToLink(),
                            Count = recipeToUse.Item2
                        }
                    },
                    new()
                    {
                        Item = new ContainerItem
                        {
                            Item = paper.ToLink(),
                            Count = recipeToUse.Item1
                        }
                    }
                };
                recipe.Conditions.Add(hasPerk);
                Console.WriteLine($"    Generated recipe");

                // Breakdown recipe logic
                breakdownRecipe.EditorID = "MAG_BreakdownRecipeScroll" + nameStripped;
                breakdownRecipe.CreatedObject = book.ToNullableLink();
                breakdownRecipe.CreatedObjectCount = 1;
                breakdownRecipe.WorkbenchKeyword = scrollWorkbenchKywd;
                var hasScrollsCondData = new GetItemCountConditionData();
                hasScrollsCondData.ItemOrList.Link.SetTo(scroll.Record);
                Condition hasScrolls = new ConditionFloat
                {
                    CompareOperator = CompareOperator.GreaterThanOrEqualTo,
                    ComparisonValue = 1.0f,
                    Data = hasScrollsCondData
                };
                Condition noPerk = new ConditionFloat
                {
                    CompareOperator = CompareOperator.EqualTo,
                    ComparisonValue = 0.0f,
                    Data = hasPerkCondData
                };
                breakdownRecipe.Items = new ExtendedList<ContainerEntry>
                {
                    new()
                    {
                        Item = new ContainerItem
                        {
                            Item = scroll.Record.ToLink(),
                            Count = 1
                        }
                    }
                };
                breakdownRecipe.Conditions.Add(noPerk);
                breakdownRecipe.Conditions.Add(hasScrolls);
                Console.WriteLine($"    Generated breakdown recipe");
            }

            foreach (var staffRecipe in state.LoadOrder.PriorityOrder.ConstructibleObject().WinningContextOverrides())
            {
                var edid = staffRecipe.Record.EditorID!;
                if (edid.Contains("MAG_") || !staffRecipe.Record.WorkbenchKeyword.Equals(vanillaStaffWorkbenchKywd)) continue;

                var newRecipe = state.PatchMod.ConstructibleObjects.GetOrAddAsOverride(staffRecipe.Record);

                if (state.LinkCache.TryResolve<IWeaponGetter>(staffRecipe.Record.CreatedObject.FormKey, out var staff))
                {
                    Console.WriteLine($"Processing staff: {staff.Name}");

                    newRecipe.EditorID += "Alt";
                    newRecipe.WorkbenchKeyword = staffWorkbenchKywd;
                    newRecipe.Items!.RemoveAt(0);

                    var ench = state.LinkCache.Resolve<IObjectEffectGetter>(staff.ObjectEffect.FormKey);
                    var max = 0.0f;
                    uint costliestEffectLevel = 0;

                    foreach (var effect in ench.Effects)
                    {
                        var record = state.LinkCache.Resolve<IMagicEffectGetter>(effect.BaseEffect.FormKey);
                        if (!(record.BaseCost > max)) continue;
                        max = record.BaseCost;
                        costliestEffectLevel = record.MinimumSkillLevel;
                    }

                    var recipes = new List<(IFormLink<ISoulGemGetter>, int)>
                    {
                        (soulGemCommon, 1),
                        (soulGemGreater, 1),
                        (soulGemGrand, 1),
                        (soulGemGrand, 2),
                        (soulGemGrand, 3),
                    };

                    var recipeToUse = costliestEffectLevel switch
                    {
                        < 25 => recipes[0],
                        >= 25 and < 50 => recipes[1],
                        >= 50 and < 75 => recipes[2],
                        >= 75 and < 100 => recipes[3],
                        >= 100 => recipes[4]
                    };

                    newRecipe.Items.Add(new ContainerEntry
                    {
                        Item = new ContainerItem
                        {
                            Item = recipeToUse.Item1,
                            Count = recipeToUse.Item2
                        }
                    });
                }
                else
                {
                    Console.WriteLine($"ERROR: Failed to process recipe {staffRecipe.Record.EditorID}");
                }
            }
        }
    }
}
