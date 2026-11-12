using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class ControlKeys
    {
        private static string searchText = "";

        internal static void DrawSettings(Rect rect)
        {
            var list = new Listing_Standard(); list.Begin(rect);
            FpsSettings settings = FpsMod.Settings;
            settings.Normalize();
            list.Label(L.T("第一人称视角灵敏度：", "First-person look sensitivity: ") + settings.lookSensitivityMultiplier.ToString("0.00") + " ×");
            settings.lookSensitivityMultiplier = Mathf.Round(list.Slider(settings.lookSensitivityMultiplier, LookSensitivityRules.Minimum, LookSensitivityRules.Maximum) * 100f) / 100f;
            list.Label(L.T("开镜灵敏度：", "Aim sensitivity: ") + settings.aimSensitivityMultiplier.ToString("0.00") + " ×");
            settings.aimSensitivityMultiplier = Mathf.Round(list.Slider(settings.aimSensitivityMultiplier, LookSensitivityRules.Minimum, LookSensitivityRules.Maximum) * 100f) / 100f;
            list.Label(L.T("数值越大转向越快；人物、炮塔和载具共用。开镜单独调节，保留镜头倍率缩放。默认 1.00 × 保持现有手感。", "Higher values turn faster. Shared by pawn, turret and vehicle views. Aim sensitivity adjusts scoped movement while preserving zoom scaling. Defaults of 1.00 × keep the existing feel."));
            if (list.ButtonText(L.T("恢复默认灵敏度", "Reset look sensitivity")))
                settings.lookSensitivityMultiplier = settings.aimSensitivityMultiplier = LookSensitivityRules.Default;
            list.Gap();
            list.Label(L.T("按键设置：点击主要键或备用键后按新键，也支持鼠标键；Esc 取消。右键按钮可清除或恢复该键。", "Key bindings: click a primary or secondary button, then press a key or mouse button. Esc cancels. Right-click a button to clear or reset it."));
            list.Label(L.T("轻按 / 长按交互键共享同一绑定；炮台切换等情境用途随之改变。更改立即生效，与游戏键盘设置共用保存。", "Tap / hold interaction uses one binding; contextual turret controls follow it. Changes apply immediately and share storage with the game's keyboard settings."));
            list.Label(L.T("轻按标记键（默认中键）标记敌人，长按打开交互轮盘；也可给“交互轮盘”单独绑定按键。移向选项后松开执行，中心或 Esc 取消。", "Tap Mark target (middle mouse by default) to mark; hold it for the interaction wheel. Interaction wheel can also have its own binding. Move to an option and release to act; center or Esc cancels."));
            if (list.ButtonText(L.T("恢复本模组全部默认按键", "Reset all bridge key bindings"))) ResetDefaults();
            if (list.ButtonText(L.T("显示第一人称键位图", "Show first-person key guide"))) FirstPersonKeyGuide.ShowNow();
            list.Label(L.T("搜索按键（名称、功能、中文或英文关键词）", "Search bindings by name, action, or Chinese / English keyword"));
            searchText = list.TextEntry(searchText);
            if (!string.IsNullOrWhiteSpace(searchText) && list.ButtonText(L.T("清除搜索", "Clear search"))) searchText = "";
            Rect title = list.GetRect(26f);
            float buttonWidth = Math.Min(135f, rect.width * .23f);
            Widgets.Label(new Rect(title.xMax - buttonWidth * 2 - 8, title.y, buttonWidth, title.height), L.T("主要键", "Primary"));
            Widgets.Label(new Rect(title.xMax - buttonWidth, title.y, buttonWidth, title.height), L.T("备用键", "Secondary"));
            foreach (ControlAction action in Actions)
            {
                KeyBindingDef def = Def(action);
                if (def == null || !Keys(action, out var keys)) continue;
                if (!MatchesSearch(action, def, keys)) continue;
                Rect row = list.GetRect(36f);
                Widgets.Label(new Rect(row.x, row.y + 3, row.width - buttonWidth * 2 - 16, 30), def.LabelCap);
                Button(new Rect(row.xMax - buttonWidth * 2 - 8, row.y, buttonWidth, 30), def, KeyPrefs.BindingSlot.A, keys.keyBindingA);
                Button(new Rect(row.xMax - buttonWidth, row.y, buttonWidth, 30), def, KeyPrefs.BindingSlot.B, keys.keyBindingB);
            }
            list.Label(L.T("Esc 关闭菜单、滚轮、数字技能栏及调速数字继续采用原有方式。按键重叠时，游戏会清除本模组中原先占用它的操作；其他模组的键盘设置保持独立。", "Esc, the wheel, numbered ability slots and speed numbers keep their existing behavior. Assigning a used bridge key clears its previous bridge action; other mods' keyboard settings remain separate."));
            list.Label(L.T("载具：机枪键（默认G）直接发射机枪；切武器键（Tab）切换武器，射击模式键（B）切换当前武器模式；装填键（R）装填所有可用武器。", "Vehicle: Machine gun (G) quick-fires machine guns; Next weapon (Tab) changes weapons; Fire mode (B) changes the selected gun's mode; Reload (R) loads all available guns."));
            list.Label(L.T("飞行器：空格上升／拉升，Ctrl下降／俯冲；双击左Ctrl自动着陆，可选着陆快捷键也可单独绑定；G直接发射机枪，也可通过 Tab 选中后左键射击。飞行升降与地面跳跃／机枪分开保存，不互相清除按键。", "Aircraft: Space ascends / pitches up; Ctrl descends / pitches down. Double left Ctrl auto lands; an optional landing shortcut can be bound below. G directly fires machine guns; Tab selection and left mouse fire remain available. Flight bindings are stored separately from ground jump / quick-fire."));
            list.End();
        }

        private static bool MatchesSearch(ControlAction action, KeyBindingDef def, KeyBindingData keys)
        {
            string needle = searchText?.Trim();
            if (string.IsNullOrEmpty(needle)) return true;
            string haystack = (def.defName + " " + def.LabelCap + " " + action + " " + Alias(action)
                + " " + keys.keyBindingA.ToStringReadable() + " " + keys.keyBindingB.ToStringReadable()).ToLowerInvariant();
            return haystack.Contains(needle.ToLowerInvariant(), StringComparison.Ordinal);
        }

        private static string Alias(ControlAction action) => action switch
        {
            ControlAction.ViewCycle => L.T("进入第一人称 切换视角", "enter first person cycle view"),
            ControlAction.ExitRecall => L.T("退出控制 返回", "exit control return"),
            ControlAction.Capture => L.T("释放 鼠标 捕获", "release capture mouse"),
            ControlAction.Forward => L.T("前进 驾驶", "forward drive"),
            ControlAction.Backward => L.T("后退", "backward"),
            ControlAction.Left => L.T("左转 左移", "left turn move"),
            ControlAction.Right => L.T("右转 右移", "right turn move"),
            ControlAction.Aim => L.T("瞄准 开镜", "aim scope"),
            ControlAction.Fire => L.T("开火 射击", "fire attack"),
            ControlAction.NextWeapon => L.T("武器 切换", "weapon cycle"),
            ControlAction.Reload => L.T("装填 换弹", "reload"),
            ControlAction.FireMode => L.T("射击模式", "fire mode"),
            ControlAction.Jump => L.T("跳跃 翻越", "jump vault"),
            ControlAction.AirAscend => L.T("上升 起飞", "aircraft ascend takeoff"),
            ControlAction.AirDescend => L.T("下降 俯冲", "aircraft descend dive"),
            ControlAction.AirLand => L.T("着陆 降落", "aircraft land"),
            ControlAction.AirOrbit => L.T("盘旋 锁定", "orbit lock"),
            ControlAction.VehicleMachineGun => L.T("载具 机枪", "vehicle machine gun"),
            _ => ""
        };

        private static void Button(Rect rect, KeyBindingDef def, KeyPrefs.BindingSlot slot, KeyCode key)
        {
            if (!Widgets.ButtonText(rect, key.ToStringReadable())) return;
            if (Event.current.button == 1)
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption(L.T("清除绑定", "Clear binding"), () => { KeyPrefs.KeyPrefsData.SetBinding(def, slot, KeyCode.None); Changed(); }),
                    new FloatMenuOption(L.T("恢复该键默认值", "Reset this binding"), () =>
                    {
                        KeyCode original = slot == KeyPrefs.BindingSlot.A ? def.defaultKeyCodeA : def.defaultKeyCodeB;
                        ResolveConflicts(def, original);
                        KeyPrefs.KeyPrefsData.EraseConflictingBindingsForKeyCode(def, original);
                        KeyPrefs.KeyPrefsData.SetBinding(def, slot, original); Changed();
                    })
                }));
            }
            else Find.WindowStack.Add(new ControlRebindDialog(def, slot));
            Event.current.Use();
        }
    }

    // Reuse the native keyboard capture/conflict UI, extending it only for mouse buttons and immediate saving.
    internal sealed class ControlRebindDialog : Dialog_DefineBinding
    {
        private readonly int openedFrame = Time.frameCount;
        private bool committed;
        public ControlRebindDialog(KeyBindingDef def, KeyPrefs.BindingSlot slot) : base(KeyPrefs.KeyPrefsData, def, slot) { }
        public override void PreOpen() { ControlKeys.Editing = true; base.PreOpen(); }
        public override void PostClose()
        {
            base.PostClose();
            if (committed) ControlKeys.ResolveConflicts(keyDef, keyPrefsData.GetBoundKeyCode(keyDef, slot));
            ControlKeys.Editing = false; ControlKeys.Changed();
        }
        public override void DoWindowContents(Rect rect)
        {
            if (Time.frameCount <= openedFrame) return;
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button >= 0 && e.button <= 6)
            {
                KeyCode code = KeyCode.Mouse0 + e.button;
                committed = true;
                keyPrefsData.EraseConflictingBindingsForKeyCode(keyDef, code);
                keyPrefsData.SetBinding(keyDef, slot, code);
                Close(); e.Use(); return;
            }
            if (e.isKey && e.type == EventType.KeyDown && e.keyCode != KeyCode.None && e.keyCode != KeyCode.Escape) committed = true;
            base.DoWindowContents(rect);
        }
    }
}
