using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Serialization;
using Assets.Scripts.UI;
using HarmonyLib;
using StationeersMods.Interface;
using UnityEngine;
using UnityEngine.UI;

namespace inventoryfixmod
{
  [StationeersMod("InventoryFixMod", "InventoryFixMod", "0.1.2")]
  class InventoryFixMod : ModBehaviour
  {
    public override void OnLoaded(ContentHandler contentHandler)
    {
      Harmony harmony = new Harmony("InventoryFixMod");
      harmony.PatchAll();
    }
  }

  [HarmonyPatch(typeof(InventoryWindowManager))]
  public static class InventoryPatch
  {
    static List<SlotDisplayButton> AllButtons;
    static Action<SlotDisplay> OnPlayerInteract;
    static FieldInfo windowField;
    static InventoryPatch()
    {
      AllButtons = typeof(InventoryWindowManager).GetField("AllButtons", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) as List<SlotDisplayButton>;
      var method = typeof(SlotDisplay).GetMethod("OnPlayerInteract", BindingFlags.Instance | BindingFlags.NonPublic);
      OnPlayerInteract = (Action<SlotDisplay>)Delegate.CreateDelegate(typeof(Action<SlotDisplay>), null, method);
      windowField = typeof(SlotDisplay).GetField("_localInventoryWindow", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static (int, int, bool) WindowToPathData(InventoryWindow window)
    {
      var fullPath = 0ul;
      var pathLen = 0;

      var slot = window.ParentSlot;
      while (slot != null && slot.Get() != InventoryWindowManager.Instance.Parent)
      {
        // add 1 to slot index so we distinguish end of path from slot 0
        fullPath <<= 8;
        fullPath |= (ulong)(slot.SlotIndex + 1) & 0xFF;
        slot = (slot.Parent as DynamicThing)?.ParentSlot;
        pathLen++;
      }

      if (pathLen > 7)
      {
        return (0, 0, false);
      }

      fullPath |= 0xFF00000000000000; // fill top byte to mark as new path format

      return ((int)fullPath, (int)(fullPath >> 32), true);
    }

    public static List<int> PathDataToPath(int slotId, int stringHash)
    {
      const ulong topByte = 0xFF00000000000000;
      var fullPath = ((ulong)slotId & 0xFFFFFFFF) | ((ulong)stringHash << 32);
      if ((fullPath & topByte) != topByte)
      {
        // old style path
        var window = InventoryWindowManager.Instance.Windows.Find(w => w.ParentSlot.StringHash == stringHash);
        if (window == null)
        {
          return null;
        }
        return new List<int> { window.ParentSlot.SlotIndex };
      }
      fullPath &= ~topByte;
      var path = new List<int>();
      while (fullPath != 0)
      {
        path.Add((int)(fullPath & 0xFF) - 1);
        fullPath >>= 8;
      }
      return path;
    }

    static InventoryWindow GetWindowByPath(List<int> path)
    {
      if (path == null || path.Count == 0)
      {
        return null;
      }

      var curThing = InventoryWindowManager.Instance.Parent as Thing;
      InventoryWindow curWindow = null;
      foreach (var slotIndex in path)
      {
        if (curThing == null || slotIndex >= curThing?.Slots?.Count)
        {
          // invalid index
          return null;
        }
        var slot = curThing.Slots[slotIndex];
        if (slot.Display == null)
        {
          return null;
        }
        OnPlayerInteract(slot.Display); // setup window if needed
        curThing = slot.Get();
        curWindow = windowField.GetValue(slot.Display) as InventoryWindow;
      }

      return curWindow;
    }

    [HarmonyPatch(nameof(InventoryWindowManager.GenerateUISaveData)), HarmonyPrefix]
    static bool GenerateUISaveData(InventoryWindowManager __instance, ref UserInterfaceSaveData __result)
    {
      UserInterfaceSaveData saveData = new UserInterfaceSaveData();
      foreach (InventoryWindow window in __instance.Windows)
      {
        if (window.GameObject == null) continue;

        var (id, hash, ok) = WindowToPathData(window);
        if (!ok) continue;
        saveData.OpenSlots.Add(new WindowSaveData()
        {
          SlotId = id,
          StringHash = hash,
          IsOpen = window.IsVisible,
          IsUndocked = window.IsUndocked,
          Position = window.RectTransform.position
        });
      }
      saveData.SelectedButton = InventoryWindowManager.CurrentButtonIndex;
      saveData.ActiveHandSlot = InventoryWindowManager.ActiveHand?.SlotIndex ?? 0;
      __result = saveData;
      return false;
    }

    [HarmonyPatch(nameof(InventoryWindowManager.LoadUserInterfaceData)), HarmonyPrefix]
    static bool LoadUserInterfaceData(UserInterfaceSaveData userInterfaceSaveData)
    {
      if (userInterfaceSaveData == null)
        return false;
      foreach (WindowSaveData openSlot in userInterfaceSaveData.OpenSlots)
      {
        var window = GetWindowByPath(PathDataToPath(openSlot.SlotId, openSlot.StringHash));
        if (window != null)
        {
          window.SetVisible(openSlot.IsOpen);
          if (openSlot.IsUndocked)
          {
            LayoutRebuilder.ForceRebuildLayoutImmediate(window.RectTransform);
            window.Undocked();
            window.RectTransform.position = (Vector3)openSlot.Position;
            window.ClampToScreen();
          }
        }
      }
      var currentScollButton = InventoryWindowManager.CurrentScollButton;
      if (AllButtons.Count > 0)
      {
        if (userInterfaceSaveData.SelectedButton >= AllButtons.Count || userInterfaceSaveData.SelectedButton < 0)
          userInterfaceSaveData.SelectedButton = 0;
        InventoryWindowManager.CurrentScollButton = AllButtons[userInterfaceSaveData.SelectedButton];
        InventoryWindowManager.CurrentScollButton.RefreshAnimation(false);
      }
      if (currentScollButton)
        currentScollButton.RefreshAnimation(false);
      if (InventoryWindowManager.ActiveHand.SlotIndex == userInterfaceSaveData.ActiveHandSlot)
      {
        if (InventoryWindowManager.ActiveHand.Get() is Tablet occupant)
          occupant.InActiveHand();
        InventoryManager.OnActiveEvent();
      }
      if (InventoryWindowManager.ActiveHand.SlotIndex != userInterfaceSaveData.ActiveHandSlot)
        Human.LocalHuman.SwapHands();
      InventoryWindowManager.WorldXmlUISaveData = null;
      return false;
    }

    [HarmonyPatch(nameof(InventoryWindowManager.ToggleWindows)), HarmonyPrefix]
    static bool ToggleWindows(bool show, InventoryWindowManager __instance)
    {
      foreach (var window in __instance.Windows)
      {
        window.GameObject.SetActive(show && window.IsVisible);
      }
      return false;
    }
  }

  [HarmonyPatch(typeof(InventoryWindow))]
  static class InventoryWindowPatch
  {
    [HarmonyPatch(nameof(InventoryWindow.SetVisible)), HarmonyPostfix]
    static void SetVisible(InventoryWindow __instance, bool isVisble)
    {
      __instance.RectTransform.gameObject.SetActive(isVisble);
    }
  }
}