using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class FirstPersonKeyGuideGame : GameComponent
    {
        public FirstPersonKeyGuideGame(Game game) { }
        public override void GameComponentUpdate() => FirstPersonKeyGuide.TryShow();
    }

    internal static class FirstPersonKeyGuide
    {
        private static bool IsAutomated => GenCommandLine.CommandLineArgPassed("fps-hidden-native")
            || GenCommandLine.CommandLineArgPassed("aircraft-footprint-native")
            || GenCommandLine.CommandLineArgPassed("vehicle-roof-mouse-native")
            || GenCommandLine.CommandLineArgPassed("fps-ready-arena");

        internal static void ShowNow()
        {
            if (Find.WindowStack == null || Find.WindowStack.Windows.Any(w => w is FirstPersonKeyGuideWindow)) return;
            FpsMod.Settings.firstPersonGuideShown = true;
            FpsMod.Settings.Write();
            Find.WindowStack.Add(new FirstPersonKeyGuideWindow());
        }

        internal static void TryShow()
        {
            if (IsAutomated || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null
                || FpsMod.Settings == null || FpsMod.Settings.firstPersonGuideShown || Find.WindowStack == null
                || LongEventHandler.AnyEventNowOrWaiting || Find.WindowStack.Windows.Any(w => w is FirstPersonKeyGuideWindow)) return;
            FpsMod.Settings.firstPersonGuideShown = true;
            FpsMod.Settings.Write();
            ShowNow();
        }
    }

    internal sealed class FirstPersonKeyGuideWindow : Window
    {
        private static readonly (string Key, string Zh, string En)[] Rows =
        {
            ("Alt + Space", "暂停／继续游戏", "Pause / resume game"),
            ("Mouse4 / P", "进入第一人称／切换视角", "Enter first person / cycle view"),
            ("Mouse3 / O", "退出控制", "Exit control"),
            ("`", "释放／捕获鼠标", "Release / capture mouse"),
            ("W A S D", "移动／驾驶", "Move / drive"),
            ("Mouse", "观察和瞄准", "Look and aim"),
            ("LMB", "开火／投掷／近战", "Fire / throw / melee"),
            ("RMB", "开镜／重击", "Aim / scope / heavy melee"),
            ("Tab", "切换武器", "Next weapon"),
            ("R", "装填", "Reload"),
            ("B", "切换射击模式", "Fire mode"),
            ("Q / hold E", "探头／倾身", "Lean left / hold to lean right"),
            ("I", "背包", "Inventory"),
            ("Space", "跳跃；飞行载具上升／起飞", "Jump; aircraft ascend / take off"),
            ("Left Ctrl", "蹲伏；直升机下降；固定翼降低推力", "Crouch; rotorcraft descend; fixed-wing throttle down"),
            ("Shift", "固定翼增加推力；达到最大空速后再次按住加力", "Fixed-wing throttle up; press again at top speed for afterburner"),
            ("L", "飞行载具自动着陆", "Aircraft auto land"),
            ("F6 / F7", "斯安威斯坦／死神之眼", "Sandevistan / Death Eye")
        };

        public FirstPersonKeyGuideWindow()
        {
            layer = WindowLayer.Dialog;
            doWindowBackground = true;
            doCloseX = false;
            doCloseButton = false;
            closeOnAccept = closeOnCancel = closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            preventCameraMotion = true;
            forcePause = true;
        }

        public override Vector2 InitialSize => new Vector2(Mathf.Min(780f, UI.screenWidth - 80f), Mathf.Min(650f, UI.screenHeight - 90f));

        public override void DoWindowContents(Rect rect)
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Close(); Event.current.Use(); return;
            }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 36f), L.T("ISORIM 第一人称键位", "ISORIM first-person controls"));
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x, rect.y + 38f, rect.width, 28f),
                L.T("按键可在模组设置的“按键设置”页改绑。Alt+空格专用于暂停。", "Rebind controls in the mod's Key bindings tab. Alt+Space is reserved for pause."));
            float y = rect.y + 78f;
            float keyWidth = 165f;
            for (int i = 0; i < Rows.Length; i++)
            {
                Rect row = new Rect(rect.x, y, rect.width, 28f);
                if ((i & 1) == 0) Widgets.DrawBoxSolid(row, new Color(.12f, .12f, .12f, .18f));
                Widgets.Label(new Rect(row.x + 8f, row.y + 3f, keyWidth, 24f), Rows[i].Key);
                Widgets.Label(new Rect(row.x + keyWidth + 8f, row.y + 3f, row.width - keyWidth - 16f, 24f), L.T(Rows[i].Zh, Rows[i].En));
                y += 29f;
            }
            Rect button = new Rect(rect.x + rect.width - 180f, rect.yMax - 38f, 180f, 32f);
            if (Widgets.ButtonText(button, L.T("知道了", "Got it"))) Close();
        }
    }
}
