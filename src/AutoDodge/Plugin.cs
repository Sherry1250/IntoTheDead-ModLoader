using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DRS.Game.Character.States;
using DRS.Game.Input;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ITD_AutoDodge
{
    [BepInPlugin("doubao.itd.autododge", "自动闪避", "1.0.0")]
    public class AutoDodgePlugin : BasePlugin
    {
        internal static ManualLogSource L;

        public override void Load()
        {
            L = Log;
            L.LogInfo("[自动闪避] 插件已加载");
            Tracker.LoadConfig();
            PatchOne(typeof(Patch_NewStateEntered));
            PatchOne(typeof(Patch_ChildStateChanged));
            PatchOne(typeof(Patch_PlayerInputs));
            PatchOne(typeof(Patch_SetInputProvider));
            PatchOne(typeof(Patch_Simulate));
            PatchOne(typeof(Patch_RootSim));
            PatchOne(typeof(Patch_Heartbeat));
            PatchOne(typeof(Patch_ProbeSetReady));
            PatchOne(typeof(Patch_DodgeSystem));
            PatchOne(typeof(Patch_Stamina));
            PatchOne(typeof(Patch_AutoQte));
            PatchOne(typeof(Patch_ExecuteAnyAngle));
            PatchOne(typeof(Patch_ProbeReadyGet));
            PatchOne(typeof(Patch_ProbeDodgeEnter));
            PatchOne(typeof(Patch_CheatGod));
            PatchOne(typeof(Patch_CheatDamageScale));
            PatchOne(typeof(Patch_CheatStamina));
            PatchOne(typeof(Patch_CheatHunger));
            PatchOne(typeof(Patch_CheatAmmo));
            PatchOne(typeof(Patch_CheatFreshShelter));
            PatchOne(typeof(Patch_CheatFreshLocation));
            PatchOne(typeof(Patch_AimAssist));
            MenuState.Load();
        }

        private static void PatchOne(Type patchType)
        {
            var harmony = new Harmony("doubao.itd.autododge." + patchType.Name);
            try
            {
                harmony.PatchAll(patchType);
                L.LogInfo("[自动闪避] 已挂补丁: " + patchType.Name);
            }
            catch (Exception e)
            {
                L.LogError("[自动闪避] " + patchType.Name + " 失败: " + e);
            }
        }
    }

    internal static class Tracker
    {
        public struct Threat
        {
            public Vector3 Pos;
            public float Time;
            public bool Used;        // 本次抬手是否已经触发过闪避
            public float HoldUntil;  // 触发后继续补写输入到这一刻，避免只写一帧闪不出去
        }

        public static readonly List<Threat> Threats = new List<Threat>();
        public static readonly HashSet<string> SeenStates = new HashSet<string>();
        // 已进入 DeadState 的角色 root，瞄准时不再锁它们
        public static readonly HashSet<IntPtr> DeadRoots = new HashSet<IntPtr>();

        public static float MinDist = 0.15f;
        public static float MaxDist = 1.6f;      // 距离窗口收紧：贴脸才闪
        public static float MaxAge = 1.0f;       // 抬手记录的有效期
        public static float WindUpDelay = 0.18f; // 抬手后先忍这么久，等手真的挥下来再闪
        public static float DelayMs = 180f;      // 滑块：抬手后多少毫秒触发自动闪避
        public static float HoldSeconds = 0.20f; // 触发后继续写入的时长，保证动作真的做出来

        static string ConfigPath
        {
            get { return System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "AutoDodge.conf"); }
        }

        static readonly string DefaultConfigText =
            "# 自动闪避参数。改完存盘，游戏里按 F11 立即生效，不用重启游戏。\r\n" +
            "# windup = 僵尸抬手后隔多久才闪（秒），越大越晚\r\n" +
            "windup=0.18\r\n" +
            "# maxdist = 超过这个距离就不闪（米）\r\n" +
            "maxdist=1.6\r\n" +
            "# mindist = 贴得太近也不闪（米）\r\n" +
            "mindist=0.15\r\n" +
            "# maxage = 一次抬手记录的有效期（秒）\r\n" +
            "maxage=1.0\r\n" +
            "# hold = 判定成立后持续写入闪避输入的时间（秒）\r\n" +
            "hold=0.20\r\n";

        public static void LoadConfig()
        {
            try
            {
                var path = ConfigPath;
                if (!System.IO.File.Exists(path))
                {
                    System.IO.File.WriteAllText(path, DefaultConfigText, new System.Text.UTF8Encoding(false));
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][配置] 已生成默认配置 " + path);
                }

                foreach (var raw in System.IO.File.ReadAllLines(path, System.Text.Encoding.UTF8))
                {
                    var line = raw;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash);
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var val = line.Substring(eq + 1).Trim();
                    float f;
                    if (!float.TryParse(val, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out f)) continue;
                    switch (key)
                    {
                        case "windup": WindUpDelay = f; DelayMs = f * 1000f; break;
                        case "maxdist": MaxDist = f; break;
                        case "mindist": MinDist = f; break;
                        case "maxage": MaxAge = f; break;
                        case "hold": HoldSeconds = f; break;
                    }
                }

                if (MaxAge < WindUpDelay + 0.05f) MaxAge = WindUpDelay + 0.05f;

                AutoDodgePlugin.L?.LogInfo("[自动闪避][配置] 已生效 windup=" + WindUpDelay.ToString("0.00")
                    + " 距离=" + MinDist.ToString("0.00") + "~" + MaxDist.ToString("0.00")
                    + " maxage=" + MaxAge.ToString("0.00") + " hold=" + HoldSeconds.ToString("0.00"));
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogError("[自动闪避][配置] 读取失败: " + e.Message);
            }
        }

        public static Vector3 PlayerPos;
        public static bool HasPlayer;

        // 自动闪避开关（菜单按钮）
        public static bool AutoDodgeOn = true;
        // 无限闪避开关（旧按钮已移除，恒关）
        public static bool InfiniteDodge;

        public static int InputCalls;
        public static int ForceDodges;
        public static int DodgeSimCalls;
        public static int StateSimCalls;
        public static int Heartbeats;

        private static FieldInfo _provField;
        private static FieldInfo _factionField;
        private static readonly HashSet<string> _examined = new HashSet<string>();

        private static FieldInfo ProvField
        {
            get { if (_provField == null) _provField = AccessTools.Field(typeof(DRS.Game.Character.CharacterRoot), "m_inputProvider"); return _provField; }
        }

        private static FieldInfo FactionField
        {
            get { if (_factionField == null) _factionField = AccessTools.Field(typeof(DRS.Game.Character.CharacterRoot), "m_faction"); return _factionField; }
        }

        private static DRS.Game.Input.ICharacterInputProvider _playerProvider;

        private static readonly System.Collections.Generic.List<KeyValuePair<IntPtr, IntPtr>> _bindings
            = new System.Collections.Generic.List<KeyValuePair<IntPtr, IntPtr>>();
        private static IntPtr _playerRootPtr;

        public static void NotePlayerProvider(DRS.Game.Input.PlayerInputProvider p)
        {
            if (p != null && _playerProvider == null)
            {
                try
                {
                    _playerProvider = p.Cast<DRS.Game.Input.ICharacterInputProvider>();
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][玩家] provider 指针 " + PtrOf(p));
                    ResolvePlayer();
                }
                catch (Exception e)
                {
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][玩家] Cast 失败: " + e.Message);
                }
            }
        }

        public static IntPtr PtrOf(object o)
        {
            try
            {
                var bob = o as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                return bob == null ? IntPtr.Zero : bob.Pointer;
            }
            catch { return IntPtr.Zero; }
        }

        public static void TryBindPlayer(DRS.Game.Character.CharacterRoot root, DRS.Game.Input.ICharacterInputProvider prov)
        {
            try
            {
                var rp = PtrOf(root);
                var pp = PtrOf(prov);
                if (rp == IntPtr.Zero || pp == IntPtr.Zero) return;
                if (_bindings.Count > 200) _bindings.Clear();
                _bindings.Add(new KeyValuePair<IntPtr, IntPtr>(rp, pp));
                ResolvePlayer();
            }
            catch { }
        }

        static void ResolvePlayer()
        {
            try
            {
                if (_stateBound) return;
                if (_playerRootPtr != IntPtr.Zero || _playerProvider == null) return;
                var target = PtrOf(_playerProvider);
                if (target == IntPtr.Zero) return;
                foreach (var b in _bindings)
                {
                    if (b.Value == target)
                    {
                        _playerRootPtr = b.Key;
                        AutoDodgePlugin.L?.LogInfo("[自动闪避][玩家] 已绑定玩家角色 root=" + b.Key
                            + "（配对 " + _bindings.Count + " 条）");
                        return;
                    }
                }
            }
            catch { }
        }

        public static string Il2CppClassName(ICharacterState st)
        {
            try
            {
                var bob = st as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                if (bob == null) return "notBase:" + st;
                var cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(bob.Pointer);
                if (cls == IntPtr.Zero) return "noclass";
                var np = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(cls);
                return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(np) ?? "null";
            }
            catch (Exception e) { return "err:" + e.Message; }
        }

        static readonly System.Collections.Generic.HashSet<string> PlayerStates =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            {
                "IdleState", "MovingState", "StandingState", "BaseLocomotingState",
                "SpeedingState", "StoppingState", "HalfTurningState", "QuarterTurningState",
                "ForwardFacingLocomotionState", "AirborneState"
            };

        public static void TryBindPlayerFromState(CharacterStateMachine sm, string stateName)
        {
            try
            {
                if (_stateBound) return;
                if (stateName == null || !PlayerStates.Contains(stateName)) return;
                var root = sm?.Root;
                if (root == null) return;
                var rp = PtrOf(root);
                if (rp == IntPtr.Zero) return;
                Vector3 pos;
                try { pos = root.CharacterTransform.position; } catch { return; }
                if (Math.Abs(pos.x) > 500f || Math.Abs(pos.y) > 500f) return;
                _playerRootPtr = rp;
                _stateBound = true;
                AutoDodgePlugin.L?.LogInfo("[自动闪避][玩家] 按状态 " + stateName + " 绑定角色 root=" + rp + " pos=" + pos);
            }
            catch { }
        }

        private static bool _stateBound;

        public static void OnState(CharacterStateMachine sm, ICharacterState st)
        {
            try
            {
                if (sm == null || st == null) return;
                string name = Il2CppClassName(st);
                TryBindPlayerFromState(sm, name);
                DiagState(name, sm);
                if (IsPlayer(sm)) { AutoQTE.NoteState(name); UpdatePosFrom(sm); return; }
                if (name.IndexOf("Dead", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        var r = sm.Root;
                        if (r != null) DeadRoots.Add(PtrOf(r));
                    }
                    catch { }
                    return;
                }
                See(sm, name);
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogInfo("[自动闪避] OnState ex " + e.Message);
            }
        }

        public static void Exam(CharacterStateMachine sm)
        {
            try
            {
                var root = sm?.Root;
                if (root == null) return;
                var tr = root.CharacterTransform;
                string name = tr != null ? tr.name : "null";
                string key = name + "|" + Probe(root);
                if (_examined.Count > 40) return;
                if (!_examined.Add(key)) return;
                AutoDodgePlugin.L?.LogInfo("[自动闪避][角色] " + key);
            }
            catch { }
        }

        private static string Probe(DRS.Game.Character.CharacterRoot root)
        {
            string prov, fac;
            try { var p = ProvField?.GetValue(root); prov = p == null ? "null" : p.GetType().Name; }
            catch { prov = "err"; }
            try { var f = FactionField?.GetValue(root); fac = f == null ? "null" : f.ToString(); }
            catch { fac = "err"; }
            return "prov=" + prov + " fac=" + fac;
        }
        private static float _lastDiag = -999f;
        private static float _lastSee = -999f;

        public static void DiagState(string name, CharacterStateMachine sm)
        {
            try
            {
                if (name == null) return;
                if (SeenStates.Count > 60) return;
                if (!SeenStates.Add(name)) return;
                string pos = "?";
                try
                {
                    var root = sm?.Root;
                    var tr = root?.CharacterTransform;
                    if (tr != null) pos = tr.position.ToString();
                }
                catch { }
                AutoDodgePlugin.L?.LogInfo("[自动闪避][状态] " + name + " @" + pos);
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogInfo("[自动闪避][状态] 异常 " + e.Message);
            }
        }

        public static void See(CharacterStateMachine sm, string stateName)
        {
            if (sm == null || stateName == null) return;
            bool windUp = stateName.IndexOf("MeleeWindUpState", StringComparison.Ordinal) >= 0;
            bool swing = stateName.IndexOf("MeleeAttackState", StringComparison.Ordinal) >= 0
                      || stateName.IndexOf("MeleeDamagingState", StringComparison.Ordinal) >= 0;
            if (!windUp && !swing) return;
            try
            {
                var root = sm.Root;
                if (root == null) return;
                var tr = root.CharacterTransform;
                if (tr == null) return;
                var pos = tr.position;

                // 抬手阶段：记录一条威胁（同位置的旧记录直接替换，保证每次挥击都能重新计）
                if (windUp)
                {
                    float nowW = Time.time;
                    for (int i = Threats.Count - 1; i >= 0; i--)
                    {
                        var old = Threats[i];
                        if (Math.Abs(old.Pos.x - pos.x) < 0.8f && Math.Abs(old.Pos.y - pos.y) < 0.8f)
                            Threats.RemoveAt(i);
                    }
                    if (Threats.Count > 60) Threats.RemoveAt(0);
                    Threats.Add(new Threat { Pos = pos, Time = nowW });
                    if (Time.time - _lastSee > 0.8f)
                    {
                        _lastSee = Time.time;
                        AutoDodgePlugin.L?.LogInfo("[自动闪避][攻击] 抬手 " + pos + " 延迟=" + Tracker.DelayMs.ToString("0") + "ms 待处理=" + Threats.Count);
                    }
                    return;
                }

                // 挥下 / 命中判定帧：把还没用过的这条提前到期，保证"快打到人"必然闪
                float now = Time.time;
                bool found = false;
                for (int i = Threats.Count - 1; i >= 0; i--)
                {
                    var old = Threats[i];
                    if (Math.Abs(old.Pos.x - pos.x) < 0.8f && Math.Abs(old.Pos.y - pos.y) < 0.8f)
                    {
                        found = true;
                        if (old.Used) return;              // 这一次挥击已经闪过了
                        var n2 = old;
                        n2.Time = now - DelayMs / 1000f - 0.001f;
                        Threats[i] = n2;
                        break;
                    }
                }
                if (!found)
                {
                    if (Threats.Count > 60) Threats.RemoveAt(0);
                    Threats.Add(new Threat { Pos = pos, Time = now - DelayMs / 1000f - 0.001f });
                }
                if (Time.time - _lastSee > 0.8f)
                {
                    _lastSee = Time.time;
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][攻击] 挥下 " + pos + " 待处理=" + Threats.Count);
                }
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogInfo("[自动闪避][攻击] 异常 " + e.Message);
            }
        }

        public static bool ShouldDodge()
        {
            Vector3 cp;
            Vector3 picked;
            if (TryPickPlayerPos(out picked)) cp = picked;
            else if (HasPlayer && Math.Abs(PlayerPos.x) < 500f && Math.Abs(PlayerPos.y) < 500f) cp = PlayerPos;
            else
            {
                var cam0 = Camera.main;
                if (cam0 == null)
                {
                    if (Time.time - _lastDiag > 5f) { _lastDiag = Time.time; AutoDodgePlugin.L?.LogInfo("[自动闪避][诊断] 无参考点"); }
                    return false;
                }
                cp = cam0.transform.position;
            }

            float now = Time.time;

            bool hit = false;
            bool hasPick = false;
            Threat pickTh = default(Threat);
            float pickedD = float.MaxValue;
            float best = float.MaxValue;
            for (int i = Threats.Count - 1; i >= 0; i--)
            {
                var th = Threats[i];
                float age = now - th.Time;
                if (age > MaxAge)
                {
                    Threats.RemoveAt(i);
                    continue;
                }
                // 横版 2D：只在 X/Y 平面比较距离，忽略相机与角色的 Z 轴差
                float dx = th.Pos.x - cp.x;
                float dy = th.Pos.y - cp.y;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                if (d < best) best = d;
                // 抬手后满 DelayMs 才允许闪；一次挥击只闪一次（Used 后不再触发）
                if (!th.Used && age >= DelayMs / 1000f && d > MinDist && d < MaxDist)
                {
                    hit = true;
                    if (d < pickedD) { pickedD = d; pickTh = th; hasPick = true; }
                }
            }

            if (Time.time - _lastDiag > 3f)
            {
                _lastDiag = Time.time;
                AutoDodgePlugin.L?.LogInfo("[自动闪避][诊断] 参考点=(" + cp.x.ToString("0.00") + "," + cp.y.ToString("0.00")
                    + ") 威胁=" + Threats.Count + " 最近=" + (best == float.MaxValue ? "-" : best.ToString("0.00")) + " 命中=" + hit);
                DumpRoots();
            }

            if (hit && hasPick)
            {
                if (now - _lastDodgeAt > 0.5f)
                {
                    _lastDodgeAt = now;
                    // 把这条威胁标成"已用 + 保持窗口"：Threat 是值类型，必须写回列表。
                    // 保持窗口让 DodgeSystem 那个入口在接下来 HoldSeconds 内持续注入 DodgeDown，
                    // 这才是真正让角色做出闪避动作的路径。
                    for (int i = 0; i < Threats.Count; i++)
                    {
                        if (Threats[i].Time == pickTh.Time && (Threats[i].Pos - pickTh.Pos).sqrMagnitude < 0.0001f)
                        {
                            var u = Threats[i];
                            u.Used = true;
                            u.Time = now;
                            u.HoldUntil = now + HoldSeconds;
                            Threats[i] = u;
                            break;
                        }
                    }
                    ForceDodges++;
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][触发] 最近僵尸 " + pickedD.ToString("0.00") + "m");
                    return true;
                }
            }
            return false;
        }

        private static float _lastDodgeAt = -999f;

        // 无节流的邻近距离判定，供闪避系统自身入口使用
        public static bool IsThreatNear()
        {
            Vector3 cp;
            Vector3 picked;
            if (TryPickPlayerPos(out picked)) cp = picked;
            else if (HasPlayer && Math.Abs(PlayerPos.x) < 500f && Math.Abs(PlayerPos.y) < 500f) cp = PlayerPos;
            else return false;

            float now = Time.time;
            for (int i = Threats.Count - 1; i >= 0; i--)
            {
                var th = Threats[i];
                float age = now - th.Time;

                // 已经触发过：在保持窗口内继续补写，让闪避系统真正吃到输入，然后作废
                if (th.Used)
                {
                    if (now < th.HoldUntil && age <= MaxAge) return true;
                    Threats.RemoveAt(i);
                    continue;
                }

                if (age > MaxAge) { Threats.RemoveAt(i); continue; }

                float dx = th.Pos.x - cp.x;
                float dy = th.Pos.y - cp.y;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                if (d <= MinDist || d >= MaxDist) continue;

                // 抬手那一刻不闪，等他快打下来才闪（延迟由菜单滑块控制）
                if (age < DelayMs / 1000f) continue;

                th.Used = true;
                th.HoldUntil = now + HoldSeconds;
                Threats[i] = th;
                AutoDodgePlugin.L?.LogInfo("[自动闪避][判定] 抬手后 " + age.ToString("0.00") + "s 距离 " + d.ToString("0.00") + "m");
                return true;
            }
            return false;
        }

        private static float _lastInputLog = -999f;

        public static bool Verbose = false;

        public static void ProbeInputs(CharacterInputs inp)
        {
            try
            {
                if (!Verbose) return;
                if (Time.time - _lastInputLog < 0.25f) return;
                _lastInputLog = Time.time;
                AutoDodgePlugin.L?.LogInfo("[自动闪避][输入] Dodge=" + inp.DodgeDown
                    + " Melee=" + inp.MeleeDown
                    + " Sprint=" + inp.SprintHeld
                    + " InteractDown=" + inp.InteractDown
                    + " InteractHeld=" + inp.InteractHeld
                    + " Grab=" + inp.GrabIsDown
                    + " Shove=" + inp.ShoveDown
                    + " H=" + inp.HorizontalAxis.ToString("0.00"));
            }
            catch { }
        }

        private static float _lastMotorLog = -999f;

        private static float _lastBlockLog = -999f;

        public static void LogBlock()
        {
            try
            {
                if (Time.time - _lastBlockLog < 1f) return;
                _lastBlockLog = Time.time;
                AutoDodgePlugin.L?.LogInfo("[自动闪避][免伤] 已拦截一次玩家伤害");
            }
            catch { }
        }

        public static void ProbeMotor(CharacterStateMachine sm)
        {
            try
            {
                if (!Verbose) return;
                if (Time.time - _lastMotorLog < 0.25f) return;
                _lastMotorLog = Time.time;
                var mc = sm?.MotorControls;
                if (mc == null) { AutoDodgePlugin.L?.LogInfo("[自动闪避][电机] null"); return; }
                AutoDodgePlugin.L?.LogInfo("[自动闪避][电机] Ready=" + mc.ReadyToDodge
                    + " Fwd=" + mc.ForwardDodge
                    + " Turn=" + mc.TurnDodge);
            }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避][电机] ex " + e.Message); }
        }

        public static readonly System.Collections.Generic.Dictionary<IntPtr, Vector3> RootPos
            = new System.Collections.Generic.Dictionary<IntPtr, Vector3>();
        public static readonly System.Collections.Generic.Dictionary<IntPtr, bool> RootPlayer
            = new System.Collections.Generic.Dictionary<IntPtr, bool>();
        public static readonly System.Collections.Generic.Dictionary<IntPtr, DRS.Game.Character.CharacterRoot> RootRef
            = new System.Collections.Generic.Dictionary<IntPtr, DRS.Game.Character.CharacterRoot>();

        public static DRS.Game.Character.CharacterRoot GetRoot(IntPtr ptr)
        {
            DRS.Game.Character.CharacterRoot r;
            if (RootRef.TryGetValue(ptr, out r) && r != null) return r;
            return null;
        }

        public static void NoteRoot(DRS.Game.Character.CharacterRoot root)
        {
            try
            {
                var p = PtrOf(root);
                if (p == IntPtr.Zero) return;
                var tr = root.CharacterTransform;
                if (tr == null) return;
                var pos = tr.position;
                if (Math.Abs(pos.x) > 500f || Math.Abs(pos.y) > 500f) return;
                RootPos[p] = pos;
                RootRef[p] = root;
                bool pl = false;
                try { if (_playerProvider != null) pl = root.IsInputProviderUsed(_playerProvider); } catch { }
                RootPlayer[p] = pl;
                if (RootPos.Count > 40) { RootPos.Clear(); RootPlayer.Clear(); RootRef.Clear(); }
            }
            catch { }
        }

        // 选玩家位置：唯一被标记为玩家的角色优先；多个则取离最近抬手僵尸最近的那个
        public static bool TryPickPlayerPos(out Vector3 pos)
        {
            pos = default(Vector3);
            int cnt = 0;
            IntPtr only = IntPtr.Zero;
            foreach (var kv in RootPlayer)
            {
                if (!kv.Value) continue;
                cnt++;
                only = kv.Key;
            }
            if (cnt == 1 && RootPos.TryGetValue(only, out pos))
            {
                _playerRootPtr = only;
                return true;
            }
            if (cnt > 1)
            {
                Vector3 refPos = default(Vector3);
                bool hasRef = false;
                if (Threats.Count > 0) { refPos = Threats[Threats.Count - 1].Pos; hasRef = true; }
                float best = float.MaxValue;
                bool found = false;
                IntPtr bestKey = IntPtr.Zero;
                foreach (var kv in RootPlayer)
                {
                    if (!kv.Value) continue;
                    Vector3 p;
                    if (!RootPos.TryGetValue(kv.Key, out p)) continue;
                    if (!hasRef) { pos = p; _playerRootPtr = kv.Key; return true; }
                    float dx = p.x - refPos.x, dy = p.y - refPos.y;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (d < best) { best = d; pos = p; bestKey = kv.Key; found = true; }
                }
                if (found) { _playerRootPtr = bestKey; return true; }
            }
            return false;
        }

        public static void DumpRoots()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in RootPos)
                {
                    bool pl = false;
                    RootPlayer.TryGetValue(kv.Key, out pl);
                    sb.Append(" [" + kv.Value.x.ToString("0.0") + "," + kv.Value.y.ToString("0.0") + "," + kv.Value.z.ToString("0.0")
                        + (pl ? " P]" : "]"));
                }
                AutoDodgePlugin.L?.LogInfo("[自动闪避][角色表]" + sb);
            }
            catch { }
        }

        public static void SetPlayerPos(Vector3 p)
        {
            PlayerPos = p;
            HasPlayer = true;
        }

        // 通过「GetInputs 调用时的当前状态机」锁定玩家：CharacterRoot.Simulate -> StateMachine.Simulate -> GetInputs
        public static IntPtr CurrentSmPtr;
        public static IntPtr PlayerSmPtr;

        public static void NoteCallerSm(CharacterStateMachine sm)
        {
            try
            {
                var p = PtrOf(sm);
                if (p != IntPtr.Zero) CurrentSmPtr = p;
            }
            catch { }
        }

        public static void BindPlayerSm()
        {
            try
            {
                if (CurrentSmPtr == IntPtr.Zero) return;
                if (PlayerSmPtr == CurrentSmPtr) return;
                PlayerSmPtr = CurrentSmPtr;
                _playerRootPtr = IntPtr.Zero;
                _stateBound = false;
                AutoDodgePlugin.L?.LogInfo("[自动闪避][玩家] 锁定状态机 ptr=" + CurrentSmPtr);
            }
            catch { }
        }

        public static bool IsPlayer(CharacterStateMachine sm)
        {
            try
            {
                if (sm == null) return false;
                var p = PtrOf(sm);
                if (PlayerSmPtr != IntPtr.Zero) return p == PlayerSmPtr;
                return IsPlayerRoot(sm.Root);
            }
            catch { return false; }
        }

        public static void UpdatePosFrom(CharacterStateMachine sm)
        {
            try
            {
                var tr = sm?.Root?.CharacterTransform;
                if (tr != null)
                {
                    var pos = tr.position;
                    if (Math.Abs(pos.x) < 500f && Math.Abs(pos.y) < 500f) SetPlayerPos(pos);
                }
            }
            catch { }
        }

        public static IntPtr PlayerRoot { get { return _playerRootPtr; } }

        // 玩家身份以模型名为准：玩家模型叫 Defender_*，僵尸模型叫 char_*
        static readonly System.Collections.Generic.Dictionary<IntPtr, int> _ident = new System.Collections.Generic.Dictionary<IntPtr, int>();
        static readonly System.Collections.Generic.Dictionary<IntPtr, float> _identAt = new System.Collections.Generic.Dictionary<IntPtr, float>();
        // 0=未知 1=玩家 2=僵尸

        public static void ClearIdentCache() { try { _ident.Clear(); _identAt.Clear(); } catch { } }

        public static int IdentOf(DRS.Game.Character.CharacterRoot root)
        {
            if (root == null) return 0;
            IntPtr p = PtrOf(root);
            if (p == IntPtr.Zero) return 0;
            int v = 0;
            float at = 0f;
            float now = Time.realtimeSinceStartup;
            if (_ident.TryGetValue(p, out v) && v != 0 && _identAt.TryGetValue(p, out at) && now - at < 2f) return v;
            v = ScanIdent(root);
            _ident[p] = v;
            _identAt[p] = now;
            return v;
        }

        static int ScanIdent(DRS.Game.Character.CharacterRoot root)
        {
            try
            {
                var t = root.CharacterTransform;
                if (t == null) return 0;
                bool zb = false;
                var sk = t.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true);
                for (int i = 0; i < sk.Length; i++)
                {
                    if (sk[i] == null) continue;
                    string n = sk[i].gameObject.name;
                    if (n == null) continue;
                    if (n.IndexOf("char_", StringComparison.OrdinalIgnoreCase) >= 0) zb = true;
                    else if (n.IndexOf("defender", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
                }
                if (zb) return 2;
                var mr = t.GetComponentsInChildren<UnityEngine.MeshRenderer>(true);
                for (int i = 0; i < mr.Length; i++)
                {
                    if (mr[i] == null) continue;
                    string n = mr[i].gameObject.name;
                    if (n == null) continue;
                    if (n.IndexOf("defender", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
                }
                return 0;
            }
            catch { return 0; }
        }

        public static bool IsPlayerRoot(DRS.Game.Character.CharacterRoot root)
        {
            try
            {
                if (root == null) return false;
                int id = IdentOf(root);
                if (id == 1) return true;      // 模型名是玩家
                if (id == 2) return false;     // 模型名是僵尸（char_），绝不当玩家
                if (_playerRootPtr == IntPtr.Zero) return false;
                return PtrOf(root) == _playerRootPtr;
            }
            catch { return false; }
        }

        // 最近的非玩家角色（僵尸）平面坐标，用于瞄准吸附
        public static bool TryNearestEnemyPos(out Vector3 pos)
        {
            pos = Vector3.zero;
            try
            {
                if (!HasPlayer) return false;
                Vector3 me = PlayerPos;
                float best = float.MaxValue;
                bool found = false;
                foreach (var kv in RootPos)
                {
                    if (kv.Key == _playerRootPtr) continue;
                    if (DeadRoots.Contains(kv.Key)) continue;
                    bool pl = false;
                    if (RootPlayer.TryGetValue(kv.Key, out pl) && pl) continue;
                    Vector3 p = kv.Value;
                    if (Math.Abs(p.x) > 500f || Math.Abs(p.y) > 500f) continue;
                    float dx = p.x - me.x, dy = p.y - me.y;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (d < 0.15f) continue;
                    if (d < best) { best = d; pos = p; found = true; }
                }
                return found;
            }
            catch { return false; }
        }

        // 取玩家角色 root 对象（瞄准线等需要世界坐标的功能用）
        public static DRS.Game.Character.CharacterRoot GetPlayerRoot()
        {
            try
            {
                DRS.Game.Character.CharacterRoot r;
                if (_playerRootPtr != IntPtr.Zero && RootRef.TryGetValue(_playerRootPtr, out r) && r != null) return r;
                foreach (var kv in RootPlayer)
                {
                    if (!kv.Value) continue;
                    if (RootRef.TryGetValue(kv.Key, out r) && r != null) return r;
                }
                // 兜底：地图场景里 RootPlayer 标记可能全为 false，改按「玩家坐标最近的 root」取
                if (HasPlayer)
                {
                    float best = 5f;
                    IntPtr bestKey = IntPtr.Zero;
                    foreach (var kv in RootPos)
                    {
                        if (!RootRef.ContainsKey(kv.Key)) continue;
                        float dx = kv.Value.x - PlayerPos.x;
                        float dy = kv.Value.y - PlayerPos.y;
                        float d = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (d < best) { best = d; bestKey = kv.Key; }
                    }
                    if (bestKey != IntPtr.Zero && RootRef.TryGetValue(bestKey, out r) && r != null) return r;
                }
            }
            catch { }
            return null;
        }

        // 重载场景后旧对象全部失效：清掉缓存引用，避免拿着已销毁对象去调用
        public static void ResetForSceneReload()
        {
            try { RootPos.Clear(); RootPlayer.Clear(); RootRef.Clear(); DeadRoots.Clear(); } catch { }
            try { _ident.Clear(); } catch { }
            try { _identAt.Clear(); } catch { }
            _playerRootPtr = IntPtr.Zero;
            _stateBound = false;
            PlayerSmPtr = IntPtr.Zero;
            CurrentSmPtr = IntPtr.Zero;
            HasPlayer = false;
            _playerProvider = null;
        }
    }

    // 枪口 → 鼠标指向位置的红线：改走 UI 覆盖层绘制，保证一定可见（不改任何资源）
    internal static class AimLine
    {
        static Canvas _cv;
        static RectTransform _rt;
        static Image _img;
        static bool _failed;
        static bool _firstDone;
        static float _dbgNext;

        // 枪口起点的屏幕像素微调（菜单上下左右按钮）
        public static float OffX;
        public static float OffY;

        public static void Nudge(float dx, float dy)
        {
            OffX += dx;
            OffY += dy;
            AutoDodgePlugin.L?.LogInfo("[瞄准线] 起点偏移 x=" + OffX.ToString("0") + " y=" + OffY.ToString("0"));
        }

        public static void ResetOffset()
        {
            OffX = 0f;
            OffY = 0f;
            AutoDodgePlugin.L?.LogInfo("[瞄准线] 起点偏移已归零");
        }

        // 只认「屏幕正中间这个人物」：候选必须投影在屏幕中心区域，再从里面挑离相机最近的。
        // 这样地图里出现别的幸存者时，不会把红线中心挂到别人身上。
        static Transform _vis;
        static float _visNext;
        static string _visTag = "-";

        static Transform GetVisual(Camera cam)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_vis != null && now < _visNext) return _vis;
                _visNext = now + 2f;
                _vis = null;
                if (cam == null) return null;
                Vector3 cp = cam.transform.position;
                var rs = UnityEngine.Object.FindObjectsOfType<UnityEngine.SkinnedMeshRenderer>();
                float bestC = float.MaxValue;
                Transform pickC = null;
                string nameC = "";
                float bestA = float.MaxValue;
                Transform pickA = null;
                string nameA = "";
                if (rs != null)
                {
                    for (int i = 0; i < rs.Length; i++)
                    {
                        var r = rs[i];
                        if (r == null) continue;
                        var t = r.transform;
                        if (t == null) continue;
                        string n = "";
                        try { n = t.gameObject.name; } catch { }
                        if (n == null) n = "";
                        string low = n.ToLower();
                        if (!(low.Contains("body") || low.Contains("torso") || low.Contains("head"))) continue;
                        float d = (t.position - cp).magnitude;
                        if (d < 0.3f) continue;
                        // 是否在屏幕中心区域（第三人称玩家就占着这里）
                        bool centered = false;
                        try
                        {
                            var vp = cam.WorldToViewportPoint(t.position);
                            if (vp.z > 0f && Math.Abs(vp.x - 0.5f) <= 0.30f && Math.Abs(vp.y - 0.5f) <= 0.45f) centered = true;
                        }
                        catch { }
                        if (centered && d < bestC) { bestC = d; pickC = t; nameC = n; }
                        if (d < bestA) { bestA = d; pickA = t; nameA = n; }
                    }
                }
                // 优先中心区域那个；中心没找到才退回最近的（避免画面里一个人都匹配不到）
                _vis = (pickC != null) ? pickC : pickA;
                _visTag = (pickC != null ? ("C:" + nameC + " d=" + bestC.ToString("0.0")) : ("A:" + nameA + " d=" + bestA.ToString("0.0")));
                return _vis;
            }
            catch { }
            return null;
        }

        // 用系统光标驱动：游戏瞄准/手电筒跟随鼠标，所以把鼠标推到目标身上就等于自动瞄准
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct WinPoint { public int X; public int Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetCursorPos(out WinPoint p);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        // 跟随平滑：0 = 光标立刻贴上去；数值越大越慢（秒）
        public static float FollowSmooth = 0f;
        static Vector2 _smoothPos;
        static bool _smoothInit;

        static void DriveCursor(Camera cam, Transform tgt)
        {
            try
            {
                // 每帧取目标的实时位置，避免只看缓存点导致一卡一卡
                Vector3 wp;
                if (tgt != null) wp = tgt.position + Vector3.up * PartOffset;
                else wp = _hasW ? _tgtW : Vector3.zero;
                Vector3 vp = cam.WorldToViewportPoint(wp);
                if (vp.z <= 0f) return;
                float tx = vp.x * Screen.width;
                float ty = Screen.height - vp.y * Screen.height;   // 系统光标 y 从上往下
                if (FollowSmooth <= 0.002f)
                {
                    SetCursorPos((int)tx, (int)ty);
                    _smoothPos = new Vector2(tx, ty);
                    _smoothInit = true;
                }
                else
                {
                    float dt = Time.unscaledDeltaTime;
                    if (dt <= 0f) dt = 0.016f;
                    if (!_smoothInit) { _smoothPos = new Vector2(tx, ty); _smoothInit = true; }
                    float k = 1f - (float)Math.Exp(-dt / FollowSmooth);
                    _smoothPos.x += (tx - _smoothPos.x) * k;
                    _smoothPos.y += (ty - _smoothPos.y) * k;
                    SetCursorPos((int)_smoothPos.x, (int)_smoothPos.y);
                }
                if (Time.realtimeSinceStartup > _curDbgNext)
                {
                    _curDbgNext = Time.realtimeSinceStartup + 2f;
                    WinPoint cp;
                    GetCursorPos(out cp);
                    AutoDodgePlugin.L?.LogInfo("[瞄准线][光标] 目标屏=(" + tx.ToString("0") + "," + ty.ToString("0")
                        + ") 平滑=" + FollowSmooth.ToString("0.000") + " 实际=(" + cp.X + "," + cp.Y + ") 目标名=" + _tgtTag
                        + " lock=" + Cursor.lockState + " visible=" + Cursor.visible);
                }
            }
            catch (Exception e)
            {
                if (Time.realtimeSinceStartup > _curDbgNext)
                {
                    _curDbgNext = Time.realtimeSinceStartup + 5f;
                    AutoDodgePlugin.L?.LogError("[瞄准线][光标] 异常 " + e.Message);
                }
            }
        }

        static float _curDbgNext;
        static bool _probeDone;

        static void ProbeOnce()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[瞄准线][探针] lock=").Append(Cursor.lockState).Append(" visible=").Append(Cursor.visible)
                  .Append(" 屏幕=").Append(Screen.width).Append("x").Append(Screen.height);
                WinPoint cp;
                if (GetCursorPos(out cp)) sb.Append(" 光标=(").Append(cp.X).Append(",").Append(cp.Y).Append(")");
                var all = UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>();
                sb.Append(" 渲染器数=").Append(all == null ? 0 : all.Length).Append(" | ");
                if (all != null)
                {
                    var names = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        var rr = all[i];
                        if (rr == null) continue;
                        string nm = "";
                        try { nm = rr.gameObject.name; } catch { }
                        string ty = "";
                        try { ty = rr.GetType().Name; } catch { }
                        string key = nm + "(" + ty + ")";
                        if (!names.Contains(key)) names.Add(key);
                        if (names.Count >= 120) break;
                    }
                    for (int i = 0; i < names.Count; i++) sb.Append(names[i]).Append(" ; ");
                }
                AutoDodgePlugin.L?.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogError("[瞄准线][探针] 异常 " + e.Message);
            }
        }

        // ===== 锁定部位：0=头 1=胸 2=腿 =====
        public static int AimPart = 0;
        public static float PartOffset
        {
            get { return AimPart == 0 ? 1.4f : (AimPart == 1 ? 0.6f : -0.2f); }
        }
        public static string PartName { get { return AimPart == 0 ? "头" : (AimPart == 1 ? "胸" : "腿"); } }

        // ===== 锁定范围框：屏幕上的矩形，只有框内的目标才会被吸附 =====
        public static bool BoxOn;
        public static float BoxL = 520f, BoxR = 520f, BoxT = 340f, BoxB = 340f;  // 以屏幕中心为基准，向四边伸出的像素
        static GameObject _bxGo;
        static RectTransform _bxT, _bxB, _bxL, _bxR;

        static RectTransform MakeEdge(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.30f, 0.95f, 0.86f, 0.60f);
            img.raycastTarget = false;
            return rt;
        }

        static void EnsureBox()
        {
            if (_bxGo != null) return;
            var cvGo = new GameObject("ITD_AimBoxUI");
            UnityEngine.Object.DontDestroyOnLoad(cvGo);
            var cv = cvGo.AddComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = -2;
            _bxGo = cvGo;
            _bxT = MakeEdge(cvGo.transform, "Top");
            _bxB = MakeEdge(cvGo.transform, "Bottom");
            _bxL = MakeEdge(cvGo.transform, "Left");
            _bxR = MakeEdge(cvGo.transform, "Right");
        }

        static void UpdateBox()
        {
            try
            {
                if (!BoxOn)
                {
                    if (_bxGo != null && _bxGo.activeSelf) _bxGo.SetActive(false);
                    return;
                }
                EnsureBox();
                if (!_bxGo.activeSelf) _bxGo.SetActive(true);
                float w = BoxL + BoxR, h = BoxT + BoxB, xm = (BoxR - BoxL) * 0.5f, ym = (BoxT - BoxB) * 0.5f;
                if (_bxT != null) { _bxT.anchoredPosition = new Vector2(xm, BoxT); _bxT.sizeDelta = new Vector2(w, 2f); }
                if (_bxB != null) { _bxB.anchoredPosition = new Vector2(xm, -BoxB); _bxB.sizeDelta = new Vector2(w, 2f); }
                if (_bxL != null) { _bxL.anchoredPosition = new Vector2(-BoxL, ym); _bxL.sizeDelta = new Vector2(2f, h); }
                if (_bxR != null) { _bxR.anchoredPosition = new Vector2(BoxR, ym); _bxR.sizeDelta = new Vector2(2f, h); }
            }
            catch { }
        }

        public static bool InBox(float sx, float sy, float sw, float sh)
        {
            float cx = sw * 0.5f, cy = sh * 0.5f;
            if (sx < cx - BoxL || sx > cx + BoxR) return false;
            if (sy < cy - BoxB || sy > cy + BoxT) return false;
            return true;
        }

        // which: 0上 1下 2左 3右
        public static void NudgeBox(int which, float delta)
        {
            try
            {
                switch (which)
                {
                    case 0: BoxT = Math.Max(80f, Math.Min(1300f, BoxT + delta)); break;
                    case 1: BoxB = Math.Max(80f, Math.Min(1300f, BoxB + delta)); break;
                    case 2: BoxL = Math.Max(80f, Math.Min(1300f, BoxL + delta)); break;
                    case 3: BoxR = Math.Max(80f, Math.Min(1300f, BoxR + delta)); break;
                }
                AutoDodgePlugin.L?.LogInfo("[瞄准线][框] L=" + BoxL.ToString("0") + " R=" + BoxR.ToString("0")
                    + " T=" + BoxT.ToString("0") + " B=" + BoxB.ToString("0"));
            }
            catch { }
        }

        public static void ResetBox()
        {
            BoxL = 520f; BoxR = 520f; BoxT = 340f; BoxB = 340f;
        }

        static string NamePrefix(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            int i = n.IndexOf('_');
            return i > 0 ? n.Substring(0, i) : n;
        }

        // 判断某个渲染 Transform 是否属于已进入 DeadState 的角色
        static bool IsDeadTransform(Transform t)
        {
            try
            {
                if (Tracker.DeadRoots.Count == 0) return false;
                Transform p = t;
                for (int k = 0; k < 8 && p != null; k++)
                {
                    foreach (var kv in Tracker.RootRef)
                    {
                        if (!Tracker.DeadRoots.Contains(kv.Key)) continue;
                        var rt = kv.Value;
                        if (rt == null) continue;
                        var ct = rt.CharacterTransform;
                        if (ct != null && ct == p) return true;
                    }
                    p = p.parent;
                }
            }
            catch { }
            return false;
        }

        // 目标锁定：找画面里离自己最近的“角色类身体”（玩家之外的就是僵尸）
        static Transform _tgt;
        static bool _hasW;
        static Vector3 _tgtW;
        static float _tgtNext;
        static string _tgtTag = "-";
        static string _tgtTagLog = "";
        static int _lastCand;

        static Transform GetTarget(Camera cam, Transform self)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now < _tgtNext) return _tgt;
                _tgtNext = now + 0.06f;
                _tgt = null;
                _tgtTag = "none";
                _hasW = false;
                _lastCand = 0;
                if (cam == null || self == null) return null;
                RefreshDeadCorpses();
                var candLog = new System.Collections.Generic.List<string>();
                // 只认名字里带 char_ 的角色模型（僵尸）；自己的部件、背包、灭火器等都不带 char_
                var rs = UnityEngine.Object.FindObjectsOfType<UnityEngine.SkinnedMeshRenderer>();
                float best = 999f;
                string name = "";
                for (int i = 0; i < rs.Length; i++)
                {
                    var r = rs[i];
                    if (r == null) continue;
                    var t = r.transform;
                    if (t == null) continue;
                    string n = "";
                    try { n = t.gameObject.name; } catch { }
                    if (n == null || n.IndexOf("char_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool act = false;
                    try { act = r.gameObject.activeInHierarchy; } catch { }
                    if (!act) continue;
                    if (IsDeadTransform(t)) continue;
                    // 只锁 Z 轴为 0 的僵尸（前后同一平面）；不在 0 的不锁
                    if (Math.Abs(t.position.z) > 0.6f) continue;
                    // 血量归零的尸体（血条已消失）不锁
                    if (IsNearDeadCorpse(t.position)) continue;
                    // 必须贴着活着的角色（有血条的），尸体模型若已脱离角色列表就会被排掉
                    if (!IsNearLive(t.position)) continue;
                    // 兜底：模型明显倒伏（躺地尸体）就不锁，即使角色列表读不到
                    try { if (Vector3.Dot(t.rotation * Vector3.up, Vector3.up) < 0.55f) continue; } catch { }
                    float d = (t.position - self.position).magnitude;
                    if (d < 0.8f || d > 60f) continue;
                    Vector3 vp;
                    try { vp = cam.WorldToViewportPoint(t.position); } catch { continue; }
                    if (vp.z <= 0f) continue;
                    if (!InBox(vp.x * Screen.width, vp.y * Screen.height, (float)Screen.width, (float)Screen.height)) continue;
                    _lastCand++;
                    if (candLog.Count < 6) candLog.Add(n + "@" + d.ToString("0.0") + " z=" + t.position.z.ToString("0.0"));
                    if (d < best) { best = d; _tgt = t; name = n; }
                }
                _tgtTag = (_tgt != null) ? (name + " d=" + best.ToString("0.0")) : "none";
                if (_tgt != null)
                {
                    _tgtW = _tgt.position + Vector3.up * PartOffset;   // 锁定 头/胸/腿
                    _hasW = true;
                }
                if (_tgtTag != _tgtTagLog)
                {
                    _tgtTagLog = _tgtTag;
                    AutoDodgePlugin.L?.LogInfo("[瞄准线][目标] " + _tgtTag + " 候选=" + _lastCand
                        + " 活体=" + _liveCount + " 死亡名单=" + Tracker.DeadRoots.Count
                        + " 名单=" + string.Join("/", candLog.ToArray()));
                }
                return _tgt;
            }
            catch { }
            return null;
        }

        // 活着的僵尸（血量>0）位置缓存；只有贴着活体位置的模型才允许锁定，尸体自然被排除
        static readonly System.Collections.Generic.List<Vector3> _livePos = new System.Collections.Generic.List<Vector3>();
        static readonly System.Collections.Generic.List<Vector3> _deadPos = new System.Collections.Generic.List<Vector3>();
        static float _deadNext;
        static float _deadLogNext;
        static int _liveCount;

        static void RefreshDeadCorpses()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _deadNext) return;
            _deadNext = now + 0.5f;
            _deadPos.Clear();
            _livePos.Clear();
            _liveCount = 0;
            var cm = HealthTags.GetCM();
            if (cm == null)
            {
                if (Time.realtimeSinceStartup > _deadLogNext) { _deadLogNext = Time.realtimeSinceStartup + 3f; AutoDodgePlugin.L?.LogInfo("[瞄准线][尸体] 取不到 CharacterManager，过滤暂失效"); }
                return;
            }
            Il2CppSystem.Collections.Generic.List<DRS.Game.Character.CharacterRoot> chars = null;
            try { chars = cm.m_characters; } catch { }
            if (chars == null)
            {
                if (Time.realtimeSinceStartup > _deadLogNext) { _deadLogNext = Time.realtimeSinceStartup + 3f; AutoDodgePlugin.L?.LogInfo("[瞄准线][尸体] 取不到角色列表，过滤暂失效"); }
                return;
            }
            for (int i = 0; i < chars.Count; i++)
            {
                try
                {
                    var cr = chars.get_Item(i);
                    if (cr == null) continue;
                    if (Tracker.IdentOf(cr) != 2) continue;   // 只要名字是 char_ 的僵尸，玩家/幸存者一律不算
                    var m = cr.Model;
                    if (m == null) continue;
                    var hp = m.Health;
                    if (hp == null) continue;
                    var t2 = cr.CharacterTransform;
                    if (t2 == null) continue;
                    if (hp.Value > 0.5f) { _livePos.Add(t2.position); _liveCount++; }
                    else _deadPos.Add(t2.position);
                }
                catch { }
            }
        }

        static bool IsNearList(Vector3 p, System.Collections.Generic.List<Vector3> list, float rx, float ry)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Vector3 d = p - list[i];
                if (Math.Abs(d.x) < rx && Math.Abs(d.z) < rx && Math.Abs(d.y) < ry) return true;
            }
            return false;
        }

        static bool IsNearDeadCorpse(Vector3 p)
        {
            return IsNearList(p, _deadPos, 2.0f, 3f);
        }

        // 尸体模型如果已经被移出角色列表，它附近不会有任何活体，直接判为不可锁
        static bool IsNearLive(Vector3 p)
        {
            if (_livePos.Count == 0) return true;   // 读不到列表时不阻塞，避免整体失效
            return IsNearList(p, _livePos, 2.0f, 3.5f);
        }

        static bool InBoxCheck(Camera cam, Transform t)
        {
            try
            {
                Vector3 vp = cam.WorldToViewportPoint(t.position);
                if (vp.z <= 0f) return false;
                return InBox(vp.x * Screen.width, vp.y * Screen.height, (float)Screen.width, (float)Screen.height);
            }
            catch { return false; }
        }

        static string ChainOf(Transform t)
        {
            if (t == null) return "-";
            var sb = new System.Text.StringBuilder();
            Transform p = t;
            for (int k = 0; k < 6 && p != null; k++)
            {
                if (sb.Length > 0) sb.Append("<");
                sb.Append(p.gameObject.name);
                p = p.parent;
            }
            return sb.ToString();
        }

        static Vector3 MousePosV3()
        {
            try
            {
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m != null)
                {
                    var v = m.position.ReadValue();
                    return new Vector3(v.x, v.y, 0f);
                }
            }
            catch { }
            return new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
        }

        static void Ensure()
        {
            if (_rt != null || _failed) return;
            try
            {
                var go = new GameObject("ITD_AimLineUI");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _cv = go.AddComponent<Canvas>();
                _cv.renderMode = RenderMode.ScreenSpaceOverlay;
                _cv.sortingOrder = -1;

                var line = new GameObject("Line");
                line.transform.SetParent(go.transform, false);
                _rt = line.AddComponent<RectTransform>();
                _rt.anchorMin = Vector2.zero;
                _rt.anchorMax = Vector2.zero;
                _rt.pivot = new Vector2(0f, 0.5f);
                _rt.sizeDelta = new Vector2(100f, 3f);
                _img = line.AddComponent<Image>();
                _img.color = new Color(1f, 0.10f, 0.08f, 0.92f);
                _img.raycastTarget = false;
                _img.enabled = false;
                AutoDodgePlugin.L?.LogInfo("[瞄准线] UI 覆盖层已创建（屏幕绘制）");
            }
            catch (Exception e)
            {
                _failed = true;
                AutoDodgePlugin.L?.LogError("[瞄准线] 创建失败 " + e);
            }
        }

        public static void Tick()
        {
            try
            {
                UpdateBox();   // 锁定框独立开关，不受瞄准线/右键影响
                bool rmb = false;
                try
                {
                    var mcur0 = UnityEngine.InputSystem.Mouse.current;
                    if (mcur0 != null) rmb = mcur0.rightButton.isPressed;
                }
                catch { }

                // ===== 鼠标锁定：独立开关，和红线开关互不影响 =====
                if (rmb && Cheats.AimLock)
                {
                    try
                    {
                        var cam0 = Camera.main;
                        if (cam0 != null)
                        {
                            Transform self0 = GetVisual(cam0);
                            if (self0 == null)
                            {
                                var root0 = Tracker.GetPlayerRoot();
                                if (root0 != null) self0 = root0.CharacterTransform;
                            }
                            if (self0 != null)
                            {
                                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                                if (!Cursor.visible) Cursor.visible = true;
                                var tg0 = GetTarget(cam0, self0);
                                if (tg0 != null) DriveCursor(cam0, tg0);
                            }
                        }
                    }
                    catch { }
                }

                if (!Cheats.AimLine)
                {
                    if (_img != null && _img.enabled) _img.enabled = false;
                    return;
                }
                // 红线只在按住右键（瞄准/手电筒键）时显示
                if (!rmb)
                {
                    if (_img != null && _img.enabled) _img.enabled = false;
                    return;
                }
                // 诊断：每 3 秒打一次状态，定位卡在哪一步
                float now = Time.realtimeSinceStartup;
                if (now >= _dbgNext)
                {
                    _dbgNext = now + 3f;
                    try
                    {
                        var c0 = Camera.main;
                        var r0 = Tracker.GetPlayerRoot();
                        var t0 = (r0 != null) ? r0.CharacterTransform : null;
                        var mpz = MousePosV3();
                        AutoDodgePlugin.L?.LogInfo("[瞄准线][诊断] flag=" + Cheats.AimLine + " cam=" + (c0 != null)
                            + " root=" + (r0 != null) + " tr=" + (t0 != null)
                            + " HasPlayer=" + Tracker.HasPlayer + " refs=" + Tracker.RootRef.Count + " roots=" + Tracker.RootPos.Count
                            + " ppos=" + Tracker.PlayerPos.ToString()
                            + " mouse=(" + mpz.x.ToString("0") + "," + mpz.y.ToString("0") + ")"
                            + " camname=" + (c0 != null ? c0.gameObject.name : "-")
                            + " campos=" + (c0 != null ? c0.transform.position.ToString() : "-")
                            + " vis=" + _visTag
                            + " visChain=" + ChainOf(_vis)
                            + " visPos=" + (_vis != null ? _vis.position.ToString() : "-")
                            + (t0 != null ? (" player=" + t0.position.ToString()) : ""));
                    }
                    catch (Exception de) { AutoDodgePlugin.L?.LogError("[瞄准线][诊断] " + de.Message); }
                }
                Ensure();
                if (_img == null) return;
                var cam = Camera.main;
                var root = Tracker.GetPlayerRoot();
                if (cam == null || root == null)
                {
                    if (_img.enabled) _img.enabled = false;
                    return;
                }
                var tr = root.CharacterTransform;
                var vis = GetVisual(cam);   // 真实渲染位置（逻辑坐标不可用于投影）
                if (tr == null && vis == null)
                {
                    if (_img.enabled) _img.enabled = false;
                    return;
                }
                Vector3 basePos = (vis != null) ? vis.position : tr.position;
                float mzH = (vis != null) ? 0.85f : 1.25f;

                // 方向：默认跟鼠标；按住右键时自动锁定画面里离自己最近的僵尸
                Vector3 mp = MousePosV3();
                var ray = cam.ScreenPointToRay(mp);
                Vector3 end;
                RaycastHit hit;
                if (Physics.Raycast(ray, out hit, 300f, ~0, QueryTriggerInteraction.Ignore)) end = hit.point;
                else end = ray.GetPoint(300f);

                float sw = (float)Screen.width;
                float sh = (float)Screen.height;

                Vector3 aimWorld = end;
                Vector2 endScr = new Vector2(mp.x, mp.y);

                Vector3 dir = aimWorld - basePos;
                float dist = dir.magnitude;
                if (dist < 0.3f)
                {
                    if (_img.enabled) _img.enabled = false;
                    return;
                }
                dir /= dist;
                Vector3 muzzle = basePos + Vector3.up * mzH + dir * 0.35f;

                // 枪口点换算到屏幕像素：用归一化视口坐标，避免 WorldToScreenPoint 的单位与窗口像素不一致
                Vector3 vp = cam.WorldToViewportPoint(muzzle);
                if (vp.z <= 0f)
                {
                    if (_img.enabled) _img.enabled = false;
                    return;
                }
                float sx = vp.x * sw + OffX;
                float sy = vp.y * sh + OffY;
                float mx = endScr.x;
                float my = endScr.y;
                float dx = mx - sx;
                float dy = my - sy;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len < 2f)
                {
                    if (_img.enabled) _img.enabled = false;
                    return;
                }
                float ang = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
                _rt.anchoredPosition = new Vector2(sx, sy);
                _rt.sizeDelta = new Vector2(len, 3f);
                _rt.localEulerAngles = new Vector3(0f, 0f, ang);
                if (!_img.enabled) _img.enabled = true;
                if (!_firstDone)
                {
                    _firstDone = true;
                    AutoDodgePlugin.L?.LogInfo("[瞄准线] 首帧 UI 线 起点屏=(" + sx.ToString("0") + "," + sy.ToString("0") + ") 屏幕=" + sw.ToString("0") + "x" + sh.ToString("0") + " 终点屏=(" + mx.ToString("0") + "," + my.ToString("0") + ") 目标=" + _tgtTag);
                }
            }
            catch (Exception e)
            {
                if (!_firstDone)
                {
                    _firstDone = true;
                    AutoDodgePlugin.L?.LogError("[瞄准线] 异常 " + e.Message);
                }
            }
        }
    }

    // 调试：把场景里所有蒙皮网格的名字打在它自己的位置上，方便人工确认哪些是僵尸
    internal static class DebugTags
    {
        public static bool On;
        static GameObject _root;
        static Font _font;
        static readonly System.Collections.Generic.List<Text> _pool = new System.Collections.Generic.List<Text>();
        static float _nextRefresh;
        static float _nextLog;

        static void Ensure()
        {
            if (_root != null) return;
            _root = new GameObject("ITD_DebugTags");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var cv = _root.AddComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 5;
            try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            for (int i = 0; i < 40; i++)
            {
                var go = new GameObject("Tag" + i);
                go.transform.SetParent(_root.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = Vector2.zero;
                rt.sizeDelta = new Vector2(340f, 22f);
                var t = go.AddComponent<Text>();
                t.font = _font;
                t.fontSize = 15;
                t.color = new Color(1f, 0.95f, 0.35f, 1f);
                t.alignment = TextAnchor.MiddleLeft;
                t.text = "";
                go.SetActive(false);
                _pool.Add(t);
            }
        }

        public static void Tick()
        {
            try
            {
                if (!On)
                {
                    if (_root != null && _root.activeSelf) _root.SetActive(false);
                    return;
                }
                Ensure();
                if (!_root.activeSelf) _root.SetActive(true);
                float now = Time.realtimeSinceStartup;
                if (now < _nextRefresh) return;
                _nextRefresh = now + 0.5f;
                Refresh();
            }
            catch { }
        }

        static void Refresh()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var rs = UnityEngine.Object.FindObjectsOfType<UnityEngine.SkinnedMeshRenderer>();
            var list = new System.Collections.Generic.List<UnityEngine.SkinnedMeshRenderer>();
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null) continue;
                bool act = false;
                try { act = r.gameObject.activeInHierarchy; } catch { }
                if (!act) continue;
                Vector3 vp;
                try { vp = cam.WorldToViewportPoint(r.transform.position); } catch { continue; }
                if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) continue;
                list.Add(r);
            }
            var camPos = cam.transform.position;
            list.Sort(delegate (UnityEngine.SkinnedMeshRenderer a, UnityEngine.SkinnedMeshRenderer b)
            {
                float da = (a.transform.position - camPos).sqrMagnitude;
                float db = (b.transform.position - camPos).sqrMagnitude;
                return da < db ? -1 : (da > db ? 1 : 0);
            });
            // 先算出每个标签的目标点，再做上下错行避让（最多错 3 行，每行 24 像素）
            var px = new System.Collections.Generic.List<float>();
            var py = new System.Collections.Generic.List<float>();
            int shown = Math.Min(list.Count, _pool.Count);
            for (int i = 0; i < shown; i++)
            {
                Vector3 vp = cam.WorldToViewportPoint(list[i].transform.position);
                px.Add(vp.x * Screen.width + 6f);
                py.Add(vp.y * Screen.height + 8f);
            }
            for (int i = 0; i < shown; i++)
            {
                float y0 = py[i];
                for (int k = 0; k < 3; k++)
                {
                    bool clash = false;
                    for (int j = 0; j < i; j++)
                    {
                        if (Math.Abs(px[j] - px[i]) < 220f && Math.Abs(py[j] - y0) < 20f) { clash = true; break; }
                    }
                    if (!clash) break;
                    y0 -= 24f;
                }
                py[i] = y0;
            }
            for (int i = 0; i < _pool.Count; i++)
            {
                if (i < shown)
                {
                    var r = list[i];
                    var rt = _pool[i].rectTransform;
                    rt.anchoredPosition = new Vector2(px[i], py[i]);
                    string nm = "";
                    try { nm = r.gameObject.name; } catch { }
                    _pool[i].text = "#" + i + " " + nm;
                    _pool[i].gameObject.SetActive(true);
                }
                else _pool[i].gameObject.SetActive(false);
            }
            if (Time.realtimeSinceStartup > _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 3f;
                var sb = new System.Text.StringBuilder();
                int cnt = Math.Min(list.Count, 40);
                for (int i = 0; i < cnt; i++)
                {
                    Vector3 wp = list[i].transform.position;
                    string nm = "";
                    try { nm = list[i].gameObject.name; } catch { }
                    sb.Append("#").Append(i).Append(" ").Append(nm)
                      .Append(" w=(").Append(wp.x.ToString("0.0")).Append(",").Append(wp.y.ToString("0.0")).Append(",").Append(wp.z.ToString("0.0")).Append(") ");
                }
                AutoDodgePlugin.L?.LogInfo("[调试][网格] 共" + list.Count + " 个可见 " + sb.ToString());
            }
        }
    }

    // 僵尸血量：在僵尸头顶显示 当前血量/最大血量，可在菜单里开关
    internal static class HealthTags
    {
        public static bool On;
        static GameObject _root;
        static Font _font;
        static readonly System.Collections.Generic.List<Text> _pool = new System.Collections.Generic.List<Text>();
        static float _next;
        static float _nextLog;

        static void Ensure()
        {
            if (_root != null) return;
            _root = new GameObject("ITD_HealthTags");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var cv = _root.AddComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 6;
            try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
            for (int i = 0; i < 24; i++)
            {
                var go = new GameObject("H" + i);
                go.transform.SetParent(_root.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = Vector2.zero;
                rt.sizeDelta = new Vector2(220f, 46f);
                var t = go.AddComponent<Text>();
                t.font = _font;
                t.fontSize = 16;
                t.color = new Color(1f, 0.28f, 0.22f, 1f);
                t.alignment = TextAnchor.MiddleCenter;
                t.text = "";
                go.SetActive(false);
                _pool.Add(t);
            }
        }

        public static void Tick()
        {
            try
            {
                if (!On)
                {
                    if (_root != null && _root.activeSelf) _root.SetActive(false);
                    return;
                }
                Ensure();
                if (!_root.activeSelf) _root.SetActive(true);
                float now = Time.realtimeSinceStartup;
                if (now < _next) return;
                _next = now + 0.25f;
                Refresh();
            }
            catch { }
        }

        static void Refresh()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var px = new System.Collections.Generic.List<float>();
            var py = new System.Collections.Generic.List<float>();
            var txt = new System.Collections.Generic.List<string>();
            int fail = 0; int total = 0;
            var cm = GetCM();
            if (cm == null)
            {
                if (Time.realtimeSinceStartup > _nextLog)
                {
                    _nextLog = Time.realtimeSinceStartup + 3f;
                    AutoDodgePlugin.L?.LogInfo("[血量] 找不到 CharacterManager");
                }
                for (int i = 0; i < _pool.Count; i++) _pool[i].gameObject.SetActive(false);
                return;
            }
            Il2CppSystem.Collections.Generic.List<DRS.Game.Character.CharacterRoot> chars = null;
            try { chars = cm.m_characters; } catch { }
            if (chars == null)
            {
                for (int i = 0; i < _pool.Count; i++) _pool[i].gameObject.SetActive(false);
                return;
            }
            for (int i = 0; i < chars.Count; i++)
            {
                DRS.Game.Character.CharacterRoot cr = null;
                try { cr = chars.get_Item(i); } catch { }
                if (cr == null) continue;
                total++;
                float cur = -1f, max = 0f; Vector3 wp = Vector3.zero;
                try
                {
                    var m = cr.Model;
                    if (m != null)
                    {
                        var hp = m.Health;
                        if (hp != null) { cur = hp.Value; max = hp.MaxValue; }
                    }
                    var t = cr.CharacterTransform;
                    if (t != null) { wp = t.position + new Vector3(0f, 1.7f, 0f); }
                }
                catch { fail++; continue; }
                if (cur < 0f) { fail++; continue; }
                // 只给名字是 char_ 的僵尸显示血量；玩家和幸存者都不显示
                if (Tracker.IdentOf(cr) != 2) continue;
                if (cur <= 0.5f) continue;
                Vector3 vp;
                try { vp = cam.WorldToViewportPoint(wp); } catch { continue; }
                if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) continue;
                px.Add(vp.x * Screen.width);
                py.Add(vp.y * Screen.height);
                txt.Add(Mathf.RoundToInt(cur) + "/" + Mathf.RoundToInt(max)
                    + "\nX " + wp.x.ToString("0.0") + "  Y " + wp.y.ToString("0.0") + "  Z " + wp.z.ToString("0.0"));
            }
            for (int i = 0; i < _pool.Count; i++)
            {
                if (i < txt.Count)
                {
                    var rt = _pool[i].rectTransform;
                    rt.anchoredPosition = new Vector2(px[i] - 110f, py[i]);
                    _pool[i].text = txt[i];
                    _pool[i].gameObject.SetActive(true);
                }
                else _pool[i].gameObject.SetActive(false);
            }
            if (Time.realtimeSinceStartup > _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 3f;
                AutoDodgePlugin.L?.LogInfo("[血量] 显示=" + txt.Count + " 角色=" + total + " 失败=" + fail
                    + (txt.Count > 0 ? " 例=" + txt[0].Replace("\n", " ") : ""));
            }
        }

        static DRS.Game.GameMain _gm;
        static DRS.Game.Character.CharacterManager _cm;
        static float _cmAt = -99f;

        // 缓存 2 秒后强制重查：退主菜单再进地图后旧 CharacterManager 会失效，不重查就永远拿空列表
        public static DRS.Game.Character.CharacterManager GetCM()
        {
            float now = Time.realtimeSinceStartup;
            try
            {
                if (_cm != null && now - _cmAt < 2f) return _cm;
                var arr = UnityEngine.Object.FindObjectsOfType<DRS.Game.GameMain>();
                if (arr != null && arr.Length > 0)
                {
                    _gm = arr[0];
                    _cm = (_gm != null) ? _gm.CharacterManager : null;
                }
                else
                {
                    _gm = null;
                    _cm = null;
                }
                _cmAt = now;
                return _cm;
            }
            catch
            {
                _gm = null;
                _cm = null;
                _cmAt = -99f;
                return null;
            }
        }
    }

    // 活僵尸身外的红色描边：复制蒙皮网格 → 沿法线外扩 → 三角面翻面（等价于正面剔除）
    // 只在外扩壳超出本体的地方露红边，看起来就是沿模型边缘的一圈红线。
    internal static class ZombieOutlineShell
    {
        public static bool On;
        public static float Width = 0.05f;

        static readonly System.Collections.Generic.Dictionary<int, GameObject> _shells =
            new System.Collections.Generic.Dictionary<int, GameObject>();
        static readonly System.Collections.Generic.Dictionary<int, Material> _mats =
            new System.Collections.Generic.Dictionary<int, Material>();
        static readonly System.Collections.Generic.List<int> _keep =
            new System.Collections.Generic.List<int>();
        static float _next;
        static float _nextLog;

        public static void Tick()
        {
            try
            {
                if (!On)
                {
                    if (_shells.Count > 0) ClearAll();
                    return;
                }
                float now = Time.realtimeSinceStartup;
                if (now < _next) return;
                _next = now + 1f;
                Rebuild();
            }
            catch (Exception e)
            {
                if (Time.realtimeSinceStartup > _nextLog)
                {
                    _nextLog = Time.realtimeSinceStartup + 5f;
                    AutoDodgePlugin.L?.LogInfo("[描边] ex " + e.Message);
                }
            }
        }

        public static void ForceRefresh()
        {
            ClearAll();
            _next = -99f;
        }

        static void ClearAll()
        {
            foreach (var kv in _shells)
            {
                var g = kv.Value;
                if (g == null) continue;
                try
                {
                    var r = g.GetComponent<SkinnedMeshRenderer>();
                    if (r != null && r.sharedMesh != null) UnityEngine.Object.Destroy(r.sharedMesh);
                }
                catch { }
                UnityEngine.Object.Destroy(g);
            }
            _shells.Clear();
        }

        static Material ShellMat(Material src)
        {
            if (src == null) return null;
            int key = src.GetInstanceID();
            Material cached;
            if (_mats.TryGetValue(key, out cached) && cached != null) return cached;
            Material m = null;
            try
            {
                if (src.shader != null) m = new Material(src.shader);
            }
            catch { }
            if (m == null) return null;
            try
            {
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                string[] texNames = { "_BaseMap", "_MainTex", "_BaseColorMap", "_AlbedoMap" };
                for (int i = 0; i < texNames.Length; i++)
                {
                    if (m.HasProperty(texNames[i])) m.SetTexture(texNames[i], tex);
                }
            }
            catch { }
            var red = new Color(0.95f, 0.10f, 0.08f, 1f);
            try { m.color = red; } catch { }
            string[] colNames = { "_BaseColor", "_Color", "_TintColor", "_MainColor", "_AlbedoColor" };
            for (int i = 0; i < colNames.Length; i++)
            {
                try { if (m.HasProperty(colNames[i])) m.SetColor(colNames[i], red); } catch { }
            }
            try { m.SetFloat("_Metallic", 0f); } catch { }
            try { m.SetFloat("_Glossiness", 0f); } catch { }
            _mats[key] = m;
            return m;
        }

        static void BuildShell(SkinnedMeshRenderer smr)
        {
            if (smr == null || smr.sharedMesh == null) return;
            Mesh mesh = null;
            try { mesh = UnityEngine.Object.Instantiate(smr.sharedMesh); } catch { }
            if (mesh == null) return;
            // 沿法线把顶点往外推，形成包住本体的一层壳
            try
            {
                var v = mesh.vertices;
                var n = mesh.normals;
                if (v != null && n != null && n.Length == v.Length)
                {
                    for (int i = 0; i < v.Length; i++) v[i] = v[i] + n[i] * Width;
                    mesh.vertices = v;
                }
            }
            catch { }
            // 三角面翻面：正面被剔除，只留背面 → 只在本体外侧露出一圈
            bool flipOk = false;
            try
            {
                int subs = mesh.subMeshCount;
                for (int s = 0; s < subs; s++)
                {
                    var t = mesh.GetTriangles(s);
                    if (t == null || t.Length < 3) continue;
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        int tmp = t[i];
                        t[i] = t[i + 2];
                        t[i + 2] = tmp;
                    }
                    mesh.SetTriangles(t, s);
                    flipOk = true;
                }
            }
            catch { }
            if (!flipOk)
            {
                // 退路：直接改整个 triangles 属性
                try
                {
                    var t = mesh.triangles;
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        int tmp = t[i];
                        t[i] = t[i + 2];
                        t[i + 2] = tmp;
                    }
                    mesh.triangles = t;
                    flipOk = true;
                }
                catch { }
            }

            var go = new GameObject("ITD_Outline");
            go.transform.SetParent(smr.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var r = go.AddComponent<SkinnedMeshRenderer>();
            r.sharedMesh = mesh;
            try { r.bones = smr.bones; } catch { }
            try { r.rootBone = smr.rootBone; } catch { }
            try { r.localBounds = smr.localBounds; } catch { }
            r.sharedMaterial = ShellMat(smr.sharedMaterial);
            try { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; } catch { }
            try { r.receiveShadows = false; } catch { }
            try { r.updateWhenOffscreen = true; } catch { }
            try { r.enabled = smr.enabled; } catch { }
            _shells[smr.GetInstanceID()] = go;
        }

        static void Rebuild()
        {
            var cm = HealthTags.GetCM();
            if (cm == null) { ClearAll(); return; }
            Il2CppSystem.Collections.Generic.List<DRS.Game.Character.CharacterRoot> chars = null;
            try { chars = cm.m_characters; } catch { }
            if (chars == null) { ClearAll(); return; }

            _keep.Clear();
            int live = 0, added = 0;
            for (int i = 0; i < chars.Count; i++)
            {
                DRS.Game.Character.CharacterRoot cr = null;
                try { cr = chars.get_Item(i); } catch { }
                if (cr == null) continue;
                if (Tracker.IdentOf(cr) != 2) continue;   // 只看名字含 char_ 的僵尸
                float hp = -1f;
                try
                {
                    var md = cr.Model;
                    if (md != null && md.Health != null) hp = md.Health.Value;
                }
                catch { }
                if (hp <= 0.5f) continue;                 // 血量归零的尸体不描
                live++;

                Transform t = null;
                try { t = cr.CharacterTransform; } catch { }
                if (t == null) continue;
                SkinnedMeshRenderer[] rs = null;
                try { rs = t.GetComponentsInChildren<SkinnedMeshRenderer>(true); } catch { }
                if (rs == null) continue;
                for (int k = 0; k < rs.Length; k++)
                {
                    var s = rs[k];
                    if (s == null) continue;
                    string nm = "";
                    try { nm = s.gameObject.name; } catch { }
                    if (nm.StartsWith("ITD_Outline")) continue;
                    if (nm.Contains("proxy_shadow") || nm.Contains("shadow_caster")) continue;
                    int id = s.GetInstanceID();
                    _keep.Add(id);
                    if (_shells.ContainsKey(id))
                    {
                        // LOD 切换时源渲染器会开关，壳跟着开关，避免几套网格的壳叠在一起
                        var g = _shells[id];
                        if (g != null)
                        {
                            var rr = g.GetComponent<SkinnedMeshRenderer>();
                            if (rr != null && rr.enabled != s.enabled) rr.enabled = s.enabled;
                        }
                        continue;
                    }
                    BuildShell(s);
                    added++;
                }
            }

            if (_shells.Count > 0)
            {
                var kill = new System.Collections.Generic.List<int>();
                foreach (var kv in _shells)
                    if (!_keep.Contains(kv.Key)) kill.Add(kv.Key);
                for (int i = 0; i < kill.Count; i++)
                {
                    GameObject g;
                    if (_shells.TryGetValue(kill[i], out g))
                    {
                        if (g != null)
                        {
                            try
                            {
                                var r = g.GetComponent<SkinnedMeshRenderer>();
                                if (r != null && r.sharedMesh != null) UnityEngine.Object.Destroy(r.sharedMesh);
                            }
                            catch { }
                            UnityEngine.Object.Destroy(g);
                        }
                        _shells.Remove(kill[i]);
                    }
                }
            }

            if (Time.realtimeSinceStartup > _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 5f;
                AutoDodgePlugin.L?.LogInfo("[描边] 活僵尸=" + live + " 新建=" + added + " 现有壳=" + _shells.Count);
            }
        }
    }

    // 僵尸屏幕方框 + 骨架 + 朝向：纯 UGUI 线段，开销很小
    internal static class ZombieOutline
    {
        public static bool On = true;
        public static bool BonesOn;        // 骨架
        public static bool DirOn = true;   // 朝向箭头
        public static float Width = 3f;    // 线粗（屏幕像素）
        public static float PadPx = 6f;    // 方框外扩像素
        public static float Hz = 30f;      // 刷新率（每秒刷新次数，越大越流畅）

        const int SLOT = 34;               // 每只僵尸固定槽位：4 方框 + 26 骨架 + 4 朝向
        const int BONE_N = 26;

        static GameObject _root;
        static Canvas _cv;
        static readonly System.Collections.Generic.List<RectTransform> _segs =
            new System.Collections.Generic.List<RectTransform>();
        static readonly System.Collections.Generic.List<Image> _imgs =
            new System.Collections.Generic.List<Image>();
        static readonly System.Collections.Generic.Dictionary<int, SkinnedMeshRenderer[]> _rsCache =
            new System.Collections.Generic.Dictionary<int, SkinnedMeshRenderer[]>();
        static readonly System.Collections.Generic.Dictionary<int, float> _rsAt =
            new System.Collections.Generic.Dictionary<int, float>();
        static readonly System.Collections.Generic.Dictionary<int, float[]> _boneCache =
            new System.Collections.Generic.Dictionary<int, float[]>();
        static readonly System.Collections.Generic.Dictionary<int, float> _boneAt =
            new System.Collections.Generic.Dictionary<int, float>();
        static readonly System.Collections.Generic.Dictionary<int, Transform> _headCache =
            new System.Collections.Generic.Dictionary<int, Transform>();

        static readonly Color ColBox = new Color(1f, 0.14f, 0.10f, 1f);
        static readonly Color ColBone = new Color(0.35f, 0.95f, 0.86f, 1f);
        static readonly Color ColDir = new Color(1f, 0.85f, 0.20f, 1f);

        static float _next;
        static float _nextLog;

        static void Ensure(int need)
        {
            if (_root == null)
            {
                _root = new GameObject("ITD_ZombieOutline");
                UnityEngine.Object.DontDestroyOnLoad(_root);
                _cv = _root.AddComponent<Canvas>();
                _cv.renderMode = RenderMode.ScreenSpaceOverlay;
                _cv.sortingOrder = 5000;
            }
            while (_segs.Count < need)
            {
                var go = new GameObject("seg");
                go.transform.SetParent(_root.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(20f, 3f);
                var img = go.AddComponent<Image>();
                img.color = ColBox;
                img.raycastTarget = false;
                go.SetActive(false);
                _segs.Add(rt);
                _imgs.Add(img);
            }
        }

        public static void Tick()
        {
            try
            {
                if (!On && !BonesOn && !DirOn)
                {
                    if (_root != null && _root.activeSelf) _root.SetActive(false);
                    return;
                }
                if (_root != null && !_root.activeSelf) _root.SetActive(true);   // 关掉再打开要把画布重新激活
                float now = Time.realtimeSinceStartup;
                if (now < _next) return;
                float hz = Hz < 1f ? 1f : Hz;
                _next = now + 1f / hz;
                Refresh();
            }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup > _nextLog)
                {
                    _nextLog = Time.realtimeSinceStartup + 5f;
                    AutoDodgePlugin.L?.LogInfo("[描边2D] ex " + ex.Message);
                }
            }
        }

        public static void ForceRefresh()
        {
            _next = -99f;
            _rsCache.Clear(); _rsAt.Clear();
            _boneCache.Clear(); _boneAt.Clear();
            _headCache.Clear();
            HideAll();
        }

        static void HideAll()
        {
            for (int i = 0; i < _segs.Count; i++)
                if (_segs[i] != null && _segs[i].gameObject.activeSelf) _segs[i].gameObject.SetActive(false);
        }

        static void HideRange(int from, int to)
        {
            for (int i = from; i < to && i < _segs.Count; i++)
                if (_segs[i] != null && _segs[i].gameObject.activeSelf) _segs[i].gameObject.SetActive(false);
        }

        static void DrawSeg(int idx, float x1, float y1, float x2, float y2, Color col)
        {
            if (idx >= _segs.Count) return;
            float dx = x2 - x1, dy = y2 - y1;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.5f) return;
            var rt = _segs[idx];
            if (!rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
            _imgs[idx].color = col;
            rt.anchoredPosition = new Vector2((x1 + x2) * 0.5f, (y1 + y2) * 0.5f);
            rt.sizeDelta = new Vector2(len + 1f, Width);
            rt.localRotation = Quaternion.Euler(0f, 0f, (float)Math.Atan2(dy, dx) * 180f / (float)Math.PI);
        }

        static SkinnedMeshRenderer[] GetRs(Transform ct, int cid, float now)
        {
            float at = 0f;
            if (_rsAt.ContainsKey(cid)) at = _rsAt[cid];
            if (_rsCache.ContainsKey(cid) && now - at < 1f) return _rsCache[cid];
            SkinnedMeshRenderer[] rs = null;
            try { rs = ct.GetComponentsInChildren<SkinnedMeshRenderer>(true); } catch { }
            _rsCache[cid] = rs;
            _rsAt[cid] = now;
            return rs;
        }

        static Transform GetHead(int cid, SkinnedMeshRenderer[] rs)
        {
            if (_headCache.ContainsKey(cid))
            {
                var ht = _headCache[cid];
                if (ht != null) return ht;
            }
            Transform found = null;
            for (int k = 0; k < rs.Length && found == null; k++)
            {
                var s = rs[k];
                if (s == null) continue;
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform> bones = null;
                try { bones = s.bones; } catch { }
                if (bones == null) continue;
                for (int b = 0; b < bones.Length; b++)
                {
                    Transform bt = null;
                    try { bt = bones[b]; } catch { }
                    if (bt == null) continue;
                    string nm = "";
                    try { nm = bt.name.ToLower(); } catch { }
                    if (nm.Contains("head") && !nm.Contains("wear") && !nm.Contains("band") && !nm.Contains("phone"))
                    {
                        found = bt; break;
                    }
                }
            }
            _headCache[cid] = found;
            return found;
        }

        // 骨架：父子骨骼连线，按屏幕长度取最长的 BONE_N 段
        static float[] BuildBones(Camera cam, SkinnedMeshRenderer[] rs)
        {
            var ids = new System.Collections.Generic.HashSet<int>();
            var list = new System.Collections.Generic.List<Transform>();
            for (int k = 0; k < rs.Length; k++)
            {
                var s = rs[k];
                if (s == null || !s.enabled || !s.gameObject.activeInHierarchy) continue;
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform> bones = null;
                try { bones = s.bones; } catch { }
                if (bones == null) continue;
                for (int b = 0; b < bones.Length; b++)
                {
                    Transform bt = null;
                    try { bt = bones[b]; } catch { }
                    if (bt == null) continue;
                    int id = 0;
                    try { id = bt.GetInstanceID(); } catch { }
                    if (ids.Contains(id)) continue;
                    ids.Add(id);
                    list.Add(bt);
                }
            }
            var segs = new System.Collections.Generic.List<float[]>();
            for (int i = 0; i < list.Count; i++)
            {
                var bt = list[i];
                Transform par = null;
                try { par = bt.parent; } catch { }
                if (par == null) continue;
                int pid = 0;
                try { pid = par.GetInstanceID(); } catch { }
                if (!ids.Contains(pid)) continue;
                Vector3 pa, pb;
                try { pa = par.position; pb = bt.position; } catch { continue; }
                if ((pb - pa).sqrMagnitude < 0.0004f) continue;
                Vector3 vpa, vpb;
                try { vpa = cam.WorldToViewportPoint(pa); vpb = cam.WorldToViewportPoint(pb); } catch { continue; }
                if (vpa.z <= 0f || vpb.z <= 0f) continue;
                float x1 = vpa.x * Screen.width, y1 = vpa.y * Screen.height;
                float x2 = vpb.x * Screen.width, y2 = vpb.y * Screen.height;
                float len = (float)Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                if (len < 6f) continue;
                segs.Add(new float[] { len, x1, y1, x2, y2 });
            }
            segs.Sort(delegate (float[] a, float[] b) { return b[0].CompareTo(a[0]); });
            var outp = new float[BONE_N * 4];
            for (int i = 0; i < outp.Length; i++) outp[i] = -99999f;
            int n = segs.Count < BONE_N ? segs.Count : BONE_N;
            for (int i = 0; i < n; i++)
            {
                outp[i * 4] = segs[i][1];
                outp[i * 4 + 1] = segs[i][2];
                outp[i * 4 + 2] = segs[i][3];
                outp[i * 4 + 3] = segs[i][4];
            }
            return outp;
        }

        static void Refresh()
        {
            var cam = Camera.main;
            var cm = HealthTags.GetCM();
            if (cam == null || cm == null) { HideAll(); return; }
            Il2CppSystem.Collections.Generic.List<DRS.Game.Character.CharacterRoot> chars = null;
            try { chars = cm.m_characters; } catch { }
            if (chars == null) { HideAll(); return; }

            float now = Time.realtimeSinceStartup;
            int used = 0;
            int zombies = 0;
            int boneUsed = 0, dirUsed = 0;

            for (int i = 0; i < chars.Count; i++)
            {
                DRS.Game.Character.CharacterRoot cr = null;
                try { cr = chars.get_Item(i); } catch { }
                if (cr == null) continue;
                if (Tracker.IdentOf(cr) != 2) continue;
                float hp = -1f;
                try
                {
                    var md = cr.Model;
                    if (md != null && md.Health != null) hp = md.Health.Value;
                }
                catch { }
                if (hp <= 0.5f) continue;
                Transform ct = null;
                try { ct = cr.CharacterTransform; } catch { }
                if (ct == null) continue;
                int cid = 0;
                try { cid = ct.GetInstanceID(); } catch { }
                var rs = GetRs(ct, cid, now);
                if (rs == null || rs.Length == 0) continue;

                float minx = 1e9f, maxx = -1e9f, miny = 1e9f, maxy = -1e9f;
                bool any = false;
                for (int k = 0; k < rs.Length; k++)
                {
                    var s = rs[k];
                    if (s == null) continue;
                    if (!s.enabled) continue;
                    if (!s.gameObject.activeInHierarchy) continue;
                    Bounds b;
                    try { b = s.bounds; } catch { continue; }
                    Vector3 c0 = b.center;
                    Vector3 ex = b.extents;
                    for (int sx = -1; sx <= 1; sx += 2)
                    {
                        for (int sy = -1; sy <= 1; sy += 2)
                        {
                            for (int sz = -1; sz <= 1; sz += 2)
                            {
                                Vector3 wp = new Vector3(c0.x + ex.x * sx, c0.y + ex.y * sy, c0.z + ex.z * sz);
                                Vector3 vp;
                                try { vp = cam.WorldToViewportPoint(wp); } catch { continue; }
                                if (vp.z <= 0f) continue;
                                float px = vp.x * Screen.width, py = vp.y * Screen.height;
                                if (px < minx) minx = px;
                                if (px > maxx) maxx = px;
                                if (py < miny) miny = py;
                                if (py > maxy) maxy = py;
                                any = true;
                            }
                        }
                    }
                }
                if (!any) continue;
                minx -= PadPx; maxx += PadPx; miny -= PadPx; maxy += PadPx;
                if (maxx < 0f || minx > Screen.width || maxy < 0f || miny > Screen.height) continue;
                zombies++;

                int baseI = used;
                used += SLOT;
                Ensure(used);

                // 方框（独立开关）
                if (On)
                {
                    DrawSeg(baseI + 0, minx, maxy, maxx, maxy, ColBox);
                    DrawSeg(baseI + 1, maxx, maxy, maxx, miny, ColBox);
                    DrawSeg(baseI + 2, maxx, miny, minx, miny, ColBox);
                    DrawSeg(baseI + 3, minx, miny, minx, maxy, ColBox);
                }
                else HideRange(baseI, baseI + 4);

                // 骨架
                if (BonesOn)
                {
                    float at = 0f;
                    if (_boneAt.ContainsKey(cid)) at = _boneAt[cid];
                    float[] bs;
                    if (_boneCache.ContainsKey(cid) && now - at < 0.07f) bs = _boneCache[cid];
                    else
                    {
                        bs = BuildBones(cam, rs);
                        _boneCache[cid] = bs;
                        _boneAt[cid] = now;
                    }
                    for (int q = 0; q < BONE_N; q++)
                    {
                        float ax = bs[q * 4];
                        if (ax <= -90000f) break;
                        DrawSeg(baseI + 4 + q, ax, bs[q * 4 + 1], bs[q * 4 + 2], bs[q * 4 + 3], ColBone);
                        boneUsed++;
                    }
                }
                else HideRange(baseI + 4, baseI + 4 + BONE_N);

                // 朝向箭头
                if (DirOn)
                {
                    try
                    {
                        var head = GetHead(cid, rs);
                        Vector3 hp3 = head != null ? head.position : new Vector3(ct.position.x, maxy == 0 ? ct.position.y : ct.position.y + 1.6f, ct.position.z);
                        if (head == null) hp3 = ct.position + Vector3.up * 1.6f;
                        Vector3 fw = ct.forward;
                        if (fw.sqrMagnitude < 0.0001f) fw = Vector3.forward;
                        Vector3 vp1 = cam.WorldToViewportPoint(hp3);
                        Vector3 vp2 = cam.WorldToViewportPoint(hp3 + fw * 0.9f);
                        if (vp1.z > 0f && vp2.z > 0f)
                        {
                            float x1 = vp1.x * Screen.width, y1 = vp1.y * Screen.height;
                            float x2 = vp2.x * Screen.width, y2 = vp2.y * Screen.height;
                            float dx = x2 - x1, dy = y2 - y1;
                            float len = (float)Math.Sqrt(dx * dx + dy * dy);
                            if (len > 8f)
                            {
                                DrawSeg(baseI + 4 + BONE_N, x1, y1, x2, y2, ColDir);
                                float ux = dx / len, uy = dy / len;
                                float al = 12f;
                                double a1 = Math.Atan2(uy, ux) + 2.6;
                                double a2 = Math.Atan2(uy, ux) - 2.6;
                                DrawSeg(baseI + 4 + BONE_N + 1, x2, y2,
                                    x2 + (float)Math.Cos(a1) * al, y2 + (float)Math.Sin(a1) * al, ColDir);
                                DrawSeg(baseI + 4 + BONE_N + 2, x2, y2,
                                    x2 + (float)Math.Cos(a2) * al, y2 + (float)Math.Sin(a2) * al, ColDir);
                                dirUsed++;
                            }
                        }
                    }
                    catch { }
                }
                else HideRange(baseI + 4 + BONE_N, baseI + 4 + BONE_N + 4);
            }

            Ensure(used);
            for (int i = used; i < _segs.Count; i++)
                if (_segs[i] != null && _segs[i].gameObject.activeSelf) _segs[i].gameObject.SetActive(false);

            if (Time.realtimeSinceStartup > _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 5f;
                AutoDodgePlugin.L?.LogInfo("[描边2D] 僵尸=" + zombies + " 段=" + used
                    + " 骨架=" + boneUsed + " 朝向=" + dirUsed + " 屏=" + Screen.width + "x" + Screen.height);
            }
        }
    }

    internal static class UI
    {
        static GameObject _canvasGo;
        static GameObject _panelGo;
        static Text _text;
        static bool _built;
        static bool _failed;
        static bool _visible;
        public static string LastAction = "无";

        static float PanelX = 20f, PanelY = 20f, PanelW = 1800f, PanelH = 1272f;
        // 菜单整体放大系数：所有按钮/文字按此比例等比放大，改一个数即可
        internal const float UiScale = 1.25f;

        static RectTransform _panelRt = null;
        static Text _infDodgeLabel;
        static Text _godTxt, _staTxt, _hungTxt, _ammoTxt, _dmgTxt, _freshTxt, _aimTxt, _aimOffTxt, _aggTxt, _aggInfoTxt, _dbgTxt, _hpTxt, _partTxt, _lockTxt, _outlineTxt, _boneTxt, _dirTxt, _fpsTxt, _aimSmTxt, _wTxt, _autoTxt, _qteTxt, _dTxt;
        public static float SpeedFactor = 1f;
        static float _speedApplied = 1f;      // 上一次由我写入的 timeScale

        static float _panelScale = 1f;
        static Vector2 _panelPos = new Vector2(PanelX, PanelY);
        static bool _dragging;
        static Vector2 _dragOff;

        class BtnDef
        {
            public string Id;
            public float X, Y, W, H;
            public Image Img;
            public Text Txt;
            public Color Base;
        }
        static readonly System.Collections.Generic.List<BtnDef> _btns = new System.Collections.Generic.List<BtnDef>();

        static readonly Color ColFunc = new Color(0.16f, 0.34f, 0.60f, 0.96f);
        static readonly Color ColGrey = new Color(0.30f, 0.30f, 0.32f, 0.96f);
        static readonly Color ColSpeed = new Color(0.22f, 0.28f, 0.38f, 0.96f);
        static readonly Color ColSpeedOn = new Color(0.16f, 0.62f, 0.30f, 0.98f);
        static readonly Color ColInf = new Color(0.62f, 0.34f, 0.10f, 0.96f);
        static readonly Color ColInfOn = new Color(0.80f, 0.32f, 0.12f, 0.98f);
        static readonly Color ColCh = new Color(0.24f, 0.30f, 0.44f, 0.96f);
        static readonly Color ColChOn = new Color(0.16f, 0.60f, 0.34f, 0.98f);

        static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        static long _lastUiMs = -1000;
        static long _lastScanMs = -1000;
        static int _lastFrame = -1;
        static bool _f1Prev, _f11Prev, _f8Prev, _f5Prev, _mlPrev;
        static long _f1LastMs = -1000, _f8LastMs = -1000, _f5LastMs = -1000, _mlLastMs = -1000;
        static bool _buildLogged;

        // 扫描场景里的角色渲染器，用于实现僵尸发光轮廓
        static float _lastScan = -100f;
        public static void ScanZombies()
        {
            if (Time.time - _lastScan < 3f) return;
            _lastScan = Time.time;
            try
            {
                var rs = UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>();
                var sb = new System.Text.StringBuilder();
                sb.Append("[扫描] renderers=" + (rs == null ? 0 : rs.Length));
                if (rs != null)
                {
                    int shown = 0;
                    for (int i = 0; i < rs.Length && shown < 25; i++)
                    {
                        var r = rs[i];
                        if (r == null) continue;
                        var mat = r.sharedMaterial;
                        string pn = "-";
                        try { if (r.transform.parent != null) pn = r.transform.parent.gameObject.name; } catch { }
                        sb.Append(" {" + r.gameObject.name + "<" + pn + "> " + r.GetType().Name + ":" + (mat != null ? mat.shader.name : "null") + "}");
                        shown++;
                    }
                }
                AutoDodgePlugin.L?.LogInfo("[自动闪避]" + sb.ToString());
            }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避][扫描] ex " + e.Message); }
        }

        static bool _shaderLogged;
        static float _lastShaderScan = -100f;
        public static void ScanShaders()
        {
            if (_shaderLogged) return;
            if (Time.time - _lastShaderScan < 3f) return;
            _lastShaderScan = Time.time;
            try
            {
                var rs = UnityEngine.Object.FindObjectsOfType<UnityEngine.Renderer>();
                if (rs == null || rs.Length == 0) return;
                var set = new System.Collections.Generic.HashSet<string>();
                UnityEngine.Material sample = null;
                UnityEngine.Material zombieSample = null;
                for (int i = 0; i < rs.Length; i++)
                {
                    var r = rs[i];
                    if (r == null) continue;
                    var m = r.sharedMaterial;
                    if (m == null) continue;
                    var sh = m.shader;
                    if (sh == null) continue;
                    var n = sh.name;
                    if (!string.IsNullOrEmpty(n)) set.Add(n);
                    if (sample == null && n == "DRS/Lit") sample = m;
                    if (zombieSample == null && r.gameObject.name.Contains("zombie")) zombieSample = m;
                }
                if (set.Count == 0) return;
                // 还没扫到僵尸材质（比如人在避难所），下次再扫
                if (sample == null && zombieSample == null) return;
                _shaderLogged = true;
                var sb = new System.Text.StringBuilder("[着色器] ");
                foreach (var s in set) sb.Append(s + ";");
                AutoDodgePlugin.L?.LogInfo("[自动闪避]" + sb.ToString());

                var probeMat = zombieSample != null ? zombieSample : sample;
                if (probeMat != null)
                {
                    string[] cand = { "_EmissionColor", "_EmissionMap", "_EmissiveColor", "_RimColor", "_RimPower",
                                      "_OutlineColor", "_OutlineWidth", "_BaseColor", "_Color", "_TintColor",
                                      "_FresnelColor", "_Cutoff", "_Alpha" };
                    var sb2 = new System.Text.StringBuilder("[材质属性] shader=" + probeMat.shader.name + " ");
                    foreach (var c in cand)
                    {
                        bool has = false;
                        try { has = probeMat.HasProperty(c); } catch { }
                        sb2.Append(c + "=" + has + " ");
                    }
                    AutoDodgePlugin.L?.LogInfo("[自动闪避]" + sb2.ToString());
                }
            }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避][着色器] ex " + e.Message); }
        }

        public static void Tick()
        {
            try
            {
                // 每个渲染帧只处理一次（Simulate 一帧会被多个角色各调一次，重复处理会让按键/点击被吃掉或双触发）
                int frame = Time.frameCount;
                if (frame == _lastFrame) return;
                _lastFrame = frame;

                // 重活（遍历 3000+ renderer 的名字与 shader）降频，避免吃掉输入采样时机
                long msNow = _sw.ElapsedMilliseconds;

                // F11 热重载配置：改 AutoDodge.conf 后按一下即可生效，不用重启游戏
                if (F11Pressed())
                {
                    Tracker.LoadConfig();
                    DumpLog();
                }

                // F8 快速重开当前地图（不用退主菜单）
                if (F8Pressed() && _sw.ElapsedMilliseconds - _f8LastMs >= 500)
                {
                    _f8LastMs = _sw.ElapsedMilliseconds;
                    QuickRestart();
                }

                // F5 立即存档
                if (F5Pressed() && _sw.ElapsedMilliseconds - _f5LastMs >= 1500)
                {
                    _f5LastMs = _sw.ElapsedMilliseconds;
                    SaveProgress();
                }

                // 场景重载后旧 UI 会被引擎销毁，重新建一次
                if (_built && _panelGo == null)
                {
                    _built = false;
                    _buildLogged = false;
                    _failed = false;
                    _visible = false;
                }

                if (!_built && !_failed && !_buildLogged)
                {
                    _buildLogged = true;
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][UI] 开始创建菜单…");
                    Build();
                }
                if (!_built) return;

                if (F1Pressed() && _sw.ElapsedMilliseconds - _f1LastMs >= 300)
                {
                    _f1LastMs = _sw.ElapsedMilliseconds;
                    _visible = !_visible;
                    _panelGo.SetActive(_visible);
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][UI] F1 -> " + (_visible ? "显示" : "隐藏"));
                    if (_visible) DumpLog();   // 打开菜单即自动导出一份诊断
                }
                if (!_visible) return;

                TickPanel();

                if (_sw.ElapsedMilliseconds - _lastUiMs < 60) return;
                _lastUiMs = _sw.ElapsedMilliseconds;
                if (_text != null) _text.text = BuildText();
            }
            catch (Exception e)
            {
                if (!_failed) { _failed = true; AutoDodgePlugin.L?.LogError("[自动闪避][UI] 异常: " + e); }
            }
        }

        // 自己锁存按下沿：OnGUI/帧时序不稳时 wasPressedThisFrame 会被漏掉，导致要按好几下
        static bool F1Pressed()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return false;
                bool now = kb.f1Key.isPressed;
                bool edge = now && !_f1Prev;
                _f1Prev = now;
                return edge;
            }
            catch { return false; }
        }

        static bool F11Pressed()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return false;
                bool now = kb.f11Key.isPressed;
                bool edge = now && !_f11Prev;
                _f11Prev = now;
                return edge;
            }
            catch { return false; }
        }

        static bool F8Pressed()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return false;
                bool now = kb.f8Key.isPressed;
                bool edge = now && !_f8Prev;
                _f8Prev = now;
                return edge;
            }
            catch { return false; }
        }

        static bool F5Pressed()
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return false;
                bool now = kb.f5Key.isPressed;
                bool edge = now && !_f5Prev;
                _f5Prev = now;
                return edge;
            }
            catch { return false; }
        }

        // 立即存档：直接走游戏自己的存档入口（和自动存档同一条路）
        static void SaveProgress()
        {
            try
            {
                var mains = UnityEngine.Object.FindObjectsOfType<DRS.Game.GameMain>();
                if (mains == null || mains.Length == 0)
                {
                    LastAction = "找不到 GameMain";
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][存档] 未找到 GameMain");
                    return;
                }
                var gm = mains[0];
                bool busy = false;
                try { busy = gm.m_gameSaveInProgress; } catch { }
                if (busy)
                {
                    LastAction = "上一次存档还没写完";
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][存档] 上一次仍在写入，忽略");
                    return;
                }
                gm.DRS_Game_IGameSaveManager_SaveGame();
                LastAction = "已保存进度";
                AutoDodgePlugin.L?.LogInfo("[自动闪避][存档] 已调用 SaveGame()");
            }
            catch (Exception e)
            {
                LastAction = "存档失败";
                AutoDodgePlugin.L?.LogInfo("[自动闪避][存档] 异常 " + e.Message);
            }
        }

        // 快速重开：让游戏的流程管理器重启当前状态（和死亡界面那颗 Restart 按钮同一条路）
        static void QuickRestart()
        {
            try
            {
                var ef = UnityEngine.Object.FindObjectsOfType<DRS.Presentation.UI.Components.EasyFlowRestart>();
                AutoDodgePlugin.L?.LogInfo("[自动闪避][重开] EasyFlowRestart 实例=" + (ef == null ? 0 : ef.Length));
                if (ef != null && ef.Length > 0)
                {
                    ef[0].Restart();
                    LastAction = "已快速重开";
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][重开] 已调用 EasyFlowRestart.Restart()");
                    return;
                }

                var fm = PikPok.TreeFlowManager.FlowManager.Instance;
                if (fm == null)
                {
                    LastAction = "找不到流程管理器";
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][重开] FlowManager.Instance 为空");
                    return;
                }
                string st = "?";
                bool busy = false;
                try { st = fm.ActiveState == null ? "?" : fm.ActiveState.ToString(); } catch { }
                try { busy = fm.IsTransitioning; } catch { }
                AutoDodgePlugin.L?.LogInfo("[自动闪避][重开] 状态=" + st + " 切换中=" + busy);
                if (busy)
                {
                    LastAction = "正在切换中，稍后再按";
                    return;
                }
                Tracker.ResetForSceneReload();
                fm.Restart();
                LastAction = "已重启流程";
                AutoDodgePlugin.L?.LogInfo("[自动闪避][重开] 已调用 FlowManager.Restart()");
            }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避][重开] 异常 " + e.Message); }
        }

        static bool MouseLeftDown(out Vector2 pos)
        {
            pos = default;
            try
            {
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m == null) return false;
                bool now = m.leftButton.isPressed;
                bool edge = now && !_mlPrev;
                _mlPrev = now;
                if (!edge) return false;
                pos = m.position.ReadValue();
                return true;
            }
            catch { }
            return false;
        }

        static Font GetFont()
        {
            Font f = null;
            string[] names = { "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "SimSun", "宋体", "Noto Sans CJK SC", "Arial" };
            foreach (var n in names)
            {
                try
                {
                    f = Font.CreateDynamicFontFromOSFont(n, 18);
                    AutoDodgePlugin.L?.LogInfo("[字体] 系统字体 \"" + n + "\" -> " + (f == null ? "null" : f.name));
                }
                catch (Exception e)
                {
                    AutoDodgePlugin.L?.LogInfo("[字体] 系统字体 \"" + n + "\" 抛异常 " + e.GetType().Name + ": " + e.Message);
                    f = null;
                }
                if (f != null && f.name != null) return f;
                f = null;
            }
            try
            {
                f = Font.CreateDynamicFontFromOSFont(names, 18);
                AutoDodgePlugin.L?.LogInfo("[字体] 数组方式 -> " + (f == null ? "null" : f.name));
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogInfo("[字体] 数组方式异常 " + e.GetType().Name + ": " + e.Message);
                f = null;
            }
            if (f != null && f.name != null) return f;
            try
            {
                var all = Font.GetOSInstalledFontNames();
                AutoDodgePlugin.L?.LogInfo("[字体] 系统字体总数=" + (all == null ? 0 : all.Length));
                if (all != null)
                {
                    int shown = 0;
                    foreach (var n in all)
                    {
                        if (n == null) continue;
                        if (n.Contains("YaHei") || n.Contains("雅黑") || n.Contains("SimHei") || n.Contains("宋") || n.Contains("黑"))
                        {
                            AutoDodgePlugin.L?.LogInfo("[字体] 候选: " + n);
                            shown++;
                            if (shown >= 8) break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogInfo("[字体] 枚举异常 " + e.GetType().Name + ": " + e.Message);
            }
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { }
            return f;
        }

        static void Build()
        {
            var font = GetFont();
            AutoDodgePlugin.L?.LogInfo(T("[自动闪避] 字体: ") + (font == null ? "null" : font.name));

            _canvasGo = new GameObject("ITD_AutoDodge_UIT(");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);

            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            canvas.scaleFactor = 1f;

            _btns.Clear();
            _texts.Clear();
            // 面板物理尺寸按屏幕自适应（给内部布局留足空间，不靠缩放）
            PanelW = Mathf.Min(1800f, Screen.width - 40f);
            PanelH = Mathf.Min(1272f, Screen.height - 60f);
            _panelGo = new GameObject("Panel");
            _panelGo.transform.SetParent(_canvasGo.transform, false);
            _panelRt = _panelGo.AddComponent<RectTransform>();
            _panelRt.anchorMin = Vector2.zero;
            _panelRt.anchorMax = Vector2.zero;
            _panelRt.pivot = Vector2.zero;
            _panelPos = new Vector2(PanelX, PanelY);
            _panelScale = 1f;
            ApplyPanelRect();
            var pimg = _panelGo.AddComponent<Image>();
            pimg.color = new Color(0f, 0f, 0f, 0.88f);

            var textGo = new GameObject("BodyT(");
            textGo.transform.SetParent(_panelGo.transform, false);
            var trt = textGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.zero;
            trt.pivot = Vector2.zero;
            trt.anchoredPosition = new Vector2(PanelW - 570f, 130f);
            trt.sizeDelta = new Vector2(530f, PanelH - 170f);
            _text = textGo.AddComponent<Text>();
            _text.font = font;
            _text.fontSize = 20;
            _text.color = Color.white;
            _text.alignment = TextAnchor.UpperLeft;
            _text.horizontalOverflow = HorizontalWrapMode.Wrap;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
            _text.supportRichText = false;

            // 第一行：功能按钮
            MakeButton(")log", T("导出日志"), 16f, 16f, 140f, 40f, font, ColFunc, 16);
            MakeButton("close", T("关闭 (F1)"), 166f, 16f, 140f, 40f, font, ColGrey, 16);
            MakeButton("zoomIn", T("放大 ＋"), 316f, 16f, 110f, 40f, font, ColGrey, 16);
            MakeButton("zoomOut", T("缩小 －"), 436f, 16f, 110f, 40f, font, ColGrey, 16);
            AddLabel(T("左键拖动菜单 · 滚轮缩放"), 558f, 24f, 220f, 24f, font, 13, new Color(0.72f, 0.75f, 0.80f));

            // 第二行：倍速档位
            AddLabel(T("倍速"), 16f, 82f, 52f, 30f, font, 15, new Color(0.85f, 0.88f, 0.92f));
            MakeButton("sp10", "1.0x", 74f, 76f, 68f, 38f, font, ColSpeed, 15);
            MakeButton("sp12", "1.2x", 146f, 76f, 68f, 38f, font, ColSpeed, 15);
            MakeButton("sp14", "1.4x", 218f, 76f, 68f, 38f, font, ColSpeed, 15);
            MakeButton("sp16", "1.6x", 290f, 76f, 68f, 38f, font, ColSpeed, 15);
            MakeButton("sp18", "1.8x", 362f, 76f, 68f, 38f, font, ColSpeed, 15);
            MakeButton("sp20", "2.0xT(", 434f, 76f, 68f, 38f, font, ColSpeed, 15);

            // 第三行：WeMod 作弊开关（按主人要求已全部移除）

            // 第四行：菜单状态保存
            AddLabel(")状态存到 BepInEx\\MenuState.conf，下次启动自动恢复", 16f, 186f, 560f, 30f, font, 14,
                new Color(0.72f, 0.78f, 0.86f));
            MakeButton("save", T("保存状态"), 596f, 184f, 140f, 38f, font, ColFunc, 15);
            _freshTxt = MakeButton("fresh", T("食物保鲜：关"), 746f, 184f, 130f, 38f, font, ColCh, 15);

            // 第五行：存档
            MakeButton("savetodisk", T("保存进度 (F5)"), 16f, 232f, 180f, 38f, font, ColFunc, 15);
            AddLabel(T("F5 随时把当前进度写盘 · 不等自动存档"), 206f, 240f, 500f, 24f, font, 14,
                new Color(0.72f, 0.78f, 0.86f));

            // 第六行：瞄准线
            _aimTxt = MakeButton("aim", T("瞄准线：开"), 16f, 280f, 160f, 38f, font, ColChOn, 15);
            AddLabel(T("枪口 → 鼠标指向 红色直线 · 不改游戏资源包"), 186f, 288f, 460f, 24f, font, 14,
                new Color(0.72f, 0.78f, 0.86f));

            // 第七行：瞄准线起点微调（屏幕像素）
            AddLabel(T("起点微调"), 16f, 336f, 76f, 24f, font, 14, new Color(0.85f, 0.88f, 0.92f));
            MakeButton("aimU", T("上 ▲"), 96f, 328f, 66f, 34f, font, ColGrey, 14);
            MakeButton("aimD", T("下 ▼"), 168f, 328f, 66f, 34f, font, ColGrey, 14);
            MakeButton("aimL", T("左 ◀"), 240f, 328f, 66f, 34f, font, ColGrey, 14);
            MakeButton("aimR", T("右 ▶"), 312f, 328f, 66f, 34f, font, ColGrey, 14);
            _aimOffTxt = MakeButton("aimReset", T("归零 0,0"), 384f, 328f, 130f, 34f, font, ColFunc, 14);

            // 第八行：锁定范围框（只有框里的僵尸会被吸附），四边可单独调
            _aggTxt = MakeButton("aimBox", T("锁定框：关"), 16f, 376f, 132f, 34f, font, ColCh, 14);
            MakeButton("aggT1", T("上+"), 156f, 376f, 52f, 34f, font, ColFunc, 13);
            MakeButton("aggT2", T("上−"), 212f, 376f, 52f, 34f, font, ColFunc, 13);
            MakeButton("aggB1", T("下+"), 272f, 376f, 52f, 34f, font, ColFunc, 13);
            MakeButton("aggB2", T("下−"), 328f, 376f, 52f, 34f, font, ColFunc, 13);
            MakeButton("aggL1", T("左+"), 388f, 376f, 52f, 34f, font, ColFunc, 13);
            MakeButton("aggL2", T("左−"), 444f, 376f, 52f, 34f, font, ColFunc, 13);
            MakeButton("aggR1", T("右+"), 504f, 376f, 52f, 34f, font, ColFunc, 13);
            MakeButton("aggR2", T("右−"), 560f, 376f, 52f, 34f, font, ColFunc, 13);
            MakeButton("aggReset", T("重置"), 620f, 376f, 90f, 34f, font, ColFunc, 13);
            _aggInfoTxt = MakeButton("aggInfo", T("框 L520 R520 T340 B340"), 16f, 418f, 360f, 30f, font, ColGrey, 12);
            _dbgTxt = MakeButton("dbgTags", T("实体标记：关"), 16f, 460f, 150f, 34f, font, ColCh, 14);
            AddLabel(T("开标记后看屏幕上 #编号+模型名，告诉我哪些是僵尸"), 176f, 462f, 520f, 30f, font, 12, Color.white);
            _hpTxt = MakeButton("hpTags", T("僵尸血量：关"), 16f, 502f, 150f, 34f, font, ColCh, 14);
            AddLabel(T("开启后在僵尸头顶显示 当前/最大 血量"), 176f, 504f, 520f, 30f, font, 12, Color.white);

            // 第十二行：锁定部位 头部 / 胸部 / 腿部
            AddLabel(T("部位："), 16f, 552f, 76f, 24f, font, 14, new Color(0.85f, 0.88f, 0.92f));
            MakeButton("aimHead", T("头部"), 96f, 544f, 84f, 34f, font, ColCh, 14);
            MakeButton("aimChest", T("胸部"), 186f, 544f, 84f, 34f, font, ColCh, 14);
            MakeButton("aimLegs", T("腿部"), 276f, 544f, 84f, 34f, font, ColCh, 14);
            AddLabel(T("按住右键时鼠标吸附到僵尸的这个部位"), 370f, 550f, 400f, 26f, font, 12, Color.white);

            // 第十三行：鼠标锁定总开关
            _lockTxt = MakeButton("aimLock", T("鼠标锁定：开"), 16f, 586f, 170f, 34f, font, ColCh, 14);
            AddLabel(T("按住右键自动吸附僵尸 · 关闭后鼠标完全跟随你"), 196f, 592f, 520f, 26f, font, 12, Color.white);

            // 第十四行：活僵尸红色方框（独立开关）
            _outlineTxt = MakeButton("outline", T("方框：关"), 16f, 628f, 170f, 34f, font, ColCh, 14);

            // 第十五行：三样各自独立开关 方框 / 骨架 / 朝向
            _boneTxt = MakeButton("bonesOn", T("骨架：关"), 196f, 628f, 150f, 34f, font, ColCh, 14);
            _dirTxt = MakeButton("dirOn", T("朝向：开"), 356f, 628f, 150f, 34f, font, ColCh, 14);
            AddLabel(T("三个开关互不影响：方框=红框 · 骨架=青色骨骼线 · 朝向=黄色箭头"), 516f, 634f, 560f, 26f, font, 12, Color.white);

            // 第十六行：线宽（滑块）
            _wTxt = AddLabel(T("线宽：3px"), 16f, 676f, 176f, 30f, font, 14, Color.white);
            BuildWSlider();
            AddLabel(T("左=线宽最细 · 右=线宽最粗"), 512f, 676f, 520f, 30f, font, 12, Color.white);

            // 第十七行：刷新率（滑块，按住拖动）
            _fpsTxt = AddLabel(T("刷新率：30/秒"), 16f, 716f, 176f, 30f, font, 14, Color.white);
            BuildSlider();
            AddLabel(T("滑块左右拖 = 每秒刷新次数 1~60（右=流畅，左=省性能）"), 512f, 716f, 520f, 30f, font, 12, Color.white);

            // 右下角缩放手柄
            MakeImage("GripBg", PanelW - 46f, 0f, 46f, 30f, new Color(0.85f, 0.55f, 0.10f, 0.85f));
            AddLabel(T("◢ 拖我缩放"), PanelW - 46f, 0f, 46f, 30f, font, 11, Color.black);

            // 第十七行：鼠标锁定跟随速度（滑块，左=立刻 右=慢慢跟）
            _aimSmTxt = AddLabel(T("跟随：立即"), 16f, 758f, 176f, 30f, font, 14, Color.white);
            BuildAimSlider();
            AddLabel(T("左=光标立刻贴上僵尸 · 右=慢慢跟过去（越右越慢）"), 512f, 758f, 520f, 30f, font, 12, Color.white);

            // 第十八行：自动闪避 / 自动QTE
            _autoTxt = MakeButton("autoDodge", T("自动闪避：开"), 16f, 820f, 176f, 38f, font, ColChOn, 15);
            _qteTxt = MakeButton("autoQte", T("自动QTE：关"), 200f, 820f, 176f, 38f, font, ColCh, 15);
            AddLabel(T("自动闪避=僵尸抬手时自动闪开 · 自动QTE=挣扎时自动连点"), 384f, 826f, 700f, 26f, font, 12, Color.white);

            // 第十九行：自动闪避延迟（滑块）
            _dTxt = AddLabel(T("闪避延迟：180ms"), 16f, 872f, 176f, 30f, font, 14, Color.white);
            BuildDSlider();
            AddLabel(T("左=抬手就闪（0ms） · 右=快打到才闪（900ms）"), 512f, 872f, 560f, 30f, font, 12, Color.white);

            // 右侧底部：语言切换
            MakeButton("langZh", T("中文"), (PanelW - 640f) / UiScale, 46f / UiScale, 120f / UiScale, 40f / UiScale, font, ColFunc, 15);
            MakeButton("langEn", "English", (PanelW - 510f) / UiScale, 46f / UiScale, 140f / UiScale, 40f / UiScale, font, ColFunc, 15);

            RefreshCheats();
            RefreshSpeedButtons();
            RefreshInfButton();

            _built = true;
            _visible = false;
            _panelGo.SetActive(false);
            LastAction = T("菜单已创建");
            AutoDodgePlugin.L?.LogInfo(T("[自动闪避] UI 菜单创建成功"));
        }

        // ===== 中英文切换 =====
        public static int Lang = 0;   // 0=中文 1=English
        static readonly System.Collections.Generic.Dictionary<string, string> _en
            = new System.Collections.Generic.Dictionary<string, string>();

        static void InitEn()
        {
            if (_en.Count > 0) { if (_rev.Count == 0) BuildRev(); return; }
            _en["导出日志"] = "Log";
            _en["关闭 (F1)"] = "Close (F1)";
            _en["放大 ＋"] = "Zoom +";
            _en["缩小 －"] = "Zoom -";
            _en["左键拖动菜单 · 滚轮缩放"] = "Drag: LMB · Zoom: wheel";
            _en["倍速"] = "Speed";
            _en["保存状态"] = "Save state";
            _en["食物保鲜：关"] = "Fresh food: OFF";
            _en["食物保鲜：开"] = "Fresh food: ON";
            _en["保存进度 (F5)"] = "Save progress (F5)";
            _en["F5 随时把当前进度写盘 · 不等自动存档"] = "F5 writes progress now, skip autosave";
            _en["瞄准线：开"] = "Aim line: ON";
            _en["瞄准线：关"] = "Aim line: OFF";
            _en["枪口 → 鼠标指向 红色直线 · 不改游戏资源包"] = "Red line: muzzle -> mouse pointer";
            _en["起点微调"] = "Start offset";
            _en["上 ▲"] = "Up ▲";
            _en["下 ▼"] = "Down ▼";
            _en["左 ◀"] = "Left ◀";
            _en["右 ▶"] = "Right ▶";
            _en["归零 0,0"] = "Reset 0,0";
            _en["锁定框：关"] = "Lock box: OFF";
            _en["锁定框：开"] = "Lock box: ON";
            _en["上+"] = "Up+";
            _en["上−"] = "Up-";
            _en["下+"] = "Dn+";
            _en["下−"] = "Dn-";
            _en["左+"] = "Lt+";
            _en["左−"] = "Lt-";
            _en["右+"] = "Rt+";
            _en["右−"] = "Rt-";
            _en["重置"] = "Reset";
            _en["实体标记：关"] = "Entity tags: OFF";
            _en["实体标记：开"] = "Entity tags: ON";
            _en["开标记后看屏幕上 #编号+模型名，告诉我哪些是僵尸"] = "Shows #id + model name on screen";
            _en["僵尸血量：关"] = "Zombie HP: OFF";
            _en["僵尸血量：开"] = "Zombie HP: ON";
            _en["开启后在僵尸头顶显示 当前/最大 血量"] = "Shows current/max HP above zombies";
            _en["锁定部位"] = "Lock part";
            _en["部位："] = "Part: ";
            _en["头部"] = "Head";
            _en["胸部"] = "Chest";
            _en["腿部"] = "Legs";
            _en["按住右键时鼠标吸附到僵尸的这个部位"] = "Cursor snaps to this part while RMB held";
            _en["鼠标锁定：开"] = "Mouse lock: ON";
            _en["鼠标锁定：关"] = "Mouse lock: OFF";
            _en["按住右键自动吸附僵尸 · 关闭后鼠标完全跟随你"] = "RMB auto-snaps to zombies; off = free cursor";
            _en["方框：开"] = "Box: ON";
            _en["方框：关"] = "Box: OFF";
            _en["骨架：开"] = "Bones: ON";
            _en["骨架：关"] = "Bones: OFF";
            _en["朝向：开"] = "Arrow: ON";
            _en["朝向：关"] = "Arrow: OFF";
            _en["三个开关互不影响：方框=红框 · 骨架=青色骨骼线 · 朝向=黄色箭头"] = "Independent: box=red · bones=cyan · arrow=yellow";
            _en["粗＋"] = "Thicker";
            _en["粗－"] = "Thinner";
            _en["线条粗细（三个都跟着变）"] = "Line width (all three follow)";
            _en["线宽："] = "Width: ";
            _en["刷新率："] = "Refresh: ";
            _en["/秒"] = "/s";
            _en["滑块左右拖 = 每秒刷新次数 1~60（右=流畅，左=省性能）"] = "Drag = refresh rate 1~60 (right=smooth, left=cheap)";
            _en["◢ 拖我缩放"] = "◢ Drag to resize";
            _en["跟随：立即"] = "Follow: instant";
            _en["跟随：慢 "] = "Follow: slow ";
            _en["ms"] = "ms";
            _en["左=光标立刻贴上僵尸 · 右=慢慢跟过去（越右越慢）"] = "Left=instant snap · Right=slow follow";
            _en["左=线宽最细 · 右=线宽最粗"] = "Left=thinnest · Right=thickest";
            _en["自动闪避：开"] = "Auto dodge: ON";
            _en["自动闪避：关"] = "Auto dodge: OFF";
            _en["自动QTE：开"] = "Auto QTE: ON";
            _en["自动QTE：关"] = "Auto QTE: OFF";
            _en["自动闪避=僵尸抬手时自动闪开 · 自动QTE=挣扎时自动连点"] = "Auto dodge = dodge on attack · Auto QTE = auto mash";
            _en["闪避延迟："] = "Dodge delay: ";
            _en["左=抬手就闪（0ms） · 右=快打到才闪（900ms）"] = "Left = dodge on wind-up · Right = dodge at the last moment";
            _en["中文"] = "中文";
            _en["English"] = "English";
            _en["语言"] = "Language";
            BuildRev();
        }

        static readonly System.Collections.Generic.Dictionary<string, string> _rev
            = new System.Collections.Generic.Dictionary<string, string>();

        static void BuildRev()
        {
            _rev.Clear();
            foreach (var kv in _en)
                if (!_rev.ContainsKey(kv.Value)) _rev[kv.Value] = kv.Key;
        }

        // 原文反查：把显示中的英文字符串找回中文原文（用于来回切换）
        static string KeyOf(string shown)
        {
            InitEn();
            string k;
            return _rev.TryGetValue(shown, out k) ? k : shown;
        }

        internal static string T(string zh)
        {
            if (Lang == 0) return zh;
            InitEn();
            string v;
            return _en.TryGetValue(zh, out v) ? v : zh;
        }

        // 切换语言：不销毁重建，只改已有文字，避免引擎侧对象失效导致崩溃
        static void SetLang(int v)
        {
            try
            {
                Lang = v;
                InitEn();
                for (int i = 0; i < _texts.Count; i++)
                {
                    Text t = _texts[i];
                    if (t == null) continue;
                    t.text = T(KeyOf(t.text));
                }
                RefreshCheats();
                RefreshSpeedButtons();
                RefreshInfButton();
                LastAction = Lang == 1 ? "Language: English" : "语言：中文";
                AutoDodgePlugin.L?.LogInfo("[自动闪避] 语言切换 " + (Lang == 1 ? "EN" : "ZH") + " 文本数=" + _texts.Count);
            }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避] 切换语言 ex " + e.Message); }
        }

        static readonly System.Collections.Generic.List<Text> _texts
            = new System.Collections.Generic.List<Text>();

        static Text MakeButton(string id, string label, float x, float y, float w, float h, Font font, Color color, int fontSize)
        {
            float ax = x * UiScale, ay = y * UiScale, aw = w * UiScale, ah = h * UiScale;
            int fs = Mathf.Max(11, Mathf.RoundToInt(fontSize * UiScale));
            var go = new GameObject("Btn_" + id);
            go.transform.SetParent(_panelGo.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(ax, ay);
            rt.sizeDelta = new Vector2(aw, ah);
            var img = go.AddComponent<Image>();
            img.color = color;

            var tg = new GameObject("Label");
            tg.transform.SetParent(go.transform, false);
            var trt = tg.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            var t = tg.AddComponent<Text>();
            t.font = font;
            t.fontSize = fs;
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.text = label;

            var bd = new BtnDef();
            bd.Id = id; bd.X = ax; bd.Y = ay; bd.W = aw; bd.H = ah;
            bd.Img = img; bd.Txt = t; bd.Base = color;
            _btns.Add(bd);
            _texts.Add(t);
            return t;
        }

        static Text AddLabel(string text, float x, float y, float w, float h, Font font, int size, Color color)
        {
            var go = new GameObject("Lbl");
            go.transform.SetParent(_panelGo.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(x * UiScale, y * UiScale);
            rt.sizeDelta = new Vector2(w * UiScale, h * UiScale);
            var t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = Mathf.Max(11, Mathf.RoundToInt(size * UiScale));
            t.color = color;
            t.alignment = TextAnchor.MiddleLeft;
            t.text = text;
            _texts.Add(t);
            return t;
        }

        static RectTransform MakeImage(string name, float x, float y, float w, float h, Color col)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_panelGo.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(x * UiScale, y * UiScale);
            rt.sizeDelta = new Vector2(w * UiScale, h * UiScale);
            var img = go.AddComponent<Image>();
            img.color = col;
            img.raycastTarget = false;
            return rt;
        }

        // 自动闪避延迟滑块（0~900ms）
        const float DSliderX = 196f, DSliderY = 866f, DSliderW = 300f;
        static RectTransform _dFillRt, _dKnobRt;
        static bool _dSliderDrag;

        static void BuildDSlider()
        {
            MakeImage("DBarBg", DSliderX, DSliderY + 8f, DSliderW, 14f, new Color(0.08f, 0.10f, 0.14f, 0.92f));
            _dFillRt = MakeImage("DFill", DSliderX, DSliderY + 8f, 10f, 14f, new Color(1f, 0.55f, 0.25f, 0.85f));
            _dKnobRt = MakeImage("DKnob", DSliderX, DSliderY + 2f, 14f, 26f, new Color(0.95f, 0.96f, 1f, 1f));
            RefreshDSlider();
        }

        static void RefreshDSlider()
        {
            try
            {
                if (_dFillRt == null || _dKnobRt == null) return;
                float t = Tracker.DelayMs / 900f;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                float bx = DSliderX * UiScale;
                float bw = (DSliderW - 14f) * UiScale;
                _dFillRt.anchoredPosition = new Vector2(bx, (DSliderY + 8f) * UiScale);
                float fw = 7f * UiScale + t * bw;
                if (fw < 2f) fw = 2f;
                _dFillRt.sizeDelta = new Vector2(fw, 14f * UiScale);
                _dKnobRt.anchoredPosition = new Vector2(bx + t * bw, (DSliderY + 2f) * UiScale);
            }
            catch { }
        }

        static void SetDFromLocalX(float localX)
        {
            try
            {
                float bx = DSliderX * UiScale;
                float bw = (DSliderW - 14f) * UiScale;
                float t = (localX - bx) / bw;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                Tracker.DelayMs = (float)Math.Round(t * 900f, 0);
                RefreshDSlider();
                if (_dTxt != null) _dTxt.text = T("闪避延迟：") + Tracker.DelayMs.ToString("0") + "ms";
                Tracker.WindUpDelay = Tracker.DelayMs / 1000f;
            }
            catch { }
        }

        static bool InDSliderArena(Vector2 local)
        {
            float bx = DSliderX * UiScale, by = (DSliderY + 2f) * UiScale;
            float bw = DSliderW * UiScale, bh = 26f * UiScale;
            return local.x >= bx - 6f && local.x <= bx + bw + 6f && local.y >= by - 6f && local.y <= by + bh + 6f;
        }

        // 线宽滑块
        const float WSliderX = 196f, WSliderY = 670f, WSliderW = 300f;
        static RectTransform _wFillRt, _wKnobRt;
        static bool _wSliderDrag;

        static void BuildWSlider()
        {
            MakeImage("WBarBg", WSliderX, WSliderY + 8f, WSliderW, 14f, new Color(0.08f, 0.10f, 0.14f, 0.92f));
            _wFillRt = MakeImage("WFill", WSliderX, WSliderY + 8f, 10f, 14f, new Color(0.30f, 0.65f, 1f, 0.85f));
            _wKnobRt = MakeImage("WKnob", WSliderX, WSliderY + 2f, 14f, 26f, new Color(0.95f, 0.96f, 1f, 1f));
            RefreshWSlider();
        }

        static void RefreshWSlider()
        {
            try
            {
                if (_wFillRt == null || _wKnobRt == null) return;
                float w = ZombieOutline.Width;
                float t = (w - 1f) / 11f;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                float bx = WSliderX * UiScale;
                float bw = (WSliderW - 14f) * UiScale;
                _wFillRt.anchoredPosition = new Vector2(bx, (WSliderY + 8f) * UiScale);
                float fw = 7f * UiScale + t * bw;
                if (fw < 2f) fw = 2f;
                _wFillRt.sizeDelta = new Vector2(fw, 14f * UiScale);
                _wKnobRt.anchoredPosition = new Vector2(bx + t * bw, (WSliderY + 2f) * UiScale);
            }
            catch { }
        }

        static void SetWFromLocalX(float localX)
        {
            try
            {
                float bx = WSliderX * UiScale;
                float bw = (WSliderW - 14f) * UiScale;
                float t = (localX - bx) / bw;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                ZombieOutline.Width = (float)Math.Round(1f + t * 11f, 0);
                RefreshWSlider();
                if (_wTxt != null) _wTxt.text = T("线宽：") + ZombieOutline.Width.ToString("0") + "px";
                ZombieOutline.ForceRefresh();
            }
            catch { }
        }

        static bool InWSliderArena(Vector2 local)
        {
            float bx = WSliderX * UiScale, by = (WSliderY + 2f) * UiScale;
            float bw = WSliderW * UiScale, bh = 26f * UiScale;
            return local.x >= bx - 6f && local.x <= bx + bw + 6f && local.y >= by - 6f && local.y <= by + bh + 6f;
        }

        // 刷新率滑块：本地坐标（已乘 UiScale）
        const float SliderX = 196f, SliderY = 712f, SliderW = 300f, SliderH = 30f;
        static RectTransform _fpsFillRt, _fpsKnobRt;
        static bool _sliderDrag, _resizeDrag;
        static Vector2 _gripLastMp;

        static void BuildSlider()
        {
            MakeImage("FpsBarBg", SliderX, SliderY + 8f, SliderW, 14f, new Color(0.08f, 0.10f, 0.14f, 0.92f));
            _fpsFillRt = MakeImage("FpsFill", SliderX, SliderY + 8f, 10f, 14f, new Color(0.30f, 0.65f, 1f, 0.85f));
            _fpsKnobRt = MakeImage("FpsKnob", SliderX, SliderY + 2f, 14f, 26f, new Color(0.95f, 0.96f, 1f, 1f));
            RefreshSlider();
        }

        static void RefreshSlider()
        {
            try
            {
                if (_fpsFillRt == null || _fpsKnobRt == null) return;
                float hz = ZombieOutline.Hz;
                if (hz < 1f) hz = 1f;
                if (hz > 60f) hz = 60f;
                float t = (hz - 1f) / 59f;
                float bx = SliderX * UiScale;
                float bw = (SliderW - 14f) * UiScale;
                float fy = (SliderY + 8f) * UiScale;
                _fpsFillRt.anchoredPosition = new Vector2(bx, fy);
                float fw = 7f * UiScale + t * bw;
                if (fw < 2f) fw = 2f;
                _fpsFillRt.sizeDelta = new Vector2(fw, 14f * UiScale);
                _fpsKnobRt.anchoredPosition = new Vector2(bx + t * bw, (SliderY + 2f) * UiScale);
            }
            catch { }
        }

        static void SetHzFromLocalX(float localX)
        {
            try
            {
                float bx = SliderX * UiScale;
                float bw = (SliderW - 14f) * UiScale;
                float t = (localX - bx) / bw;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                float hz = 1f + t * 59f;
                ZombieOutline.Hz = Mathf.Round(hz);
                RefreshSlider();
                if (_fpsTxt != null) _fpsTxt.text = "刷新率：" + ZombieOutline.Hz.ToString("0") + "/秒";
            }
            catch { }
        }

        static bool InSliderArena(Vector2 local)
        {
            float bx = SliderX * UiScale, by = (SliderY + 2f) * UiScale;
            float bw = SliderW * UiScale, bh = 26f * UiScale;
            return local.x >= bx - 6f && local.x <= bx + bw + 6f && local.y >= by - 6f && local.y <= by + bh + 6f;
        }

        static bool InGrip(Vector2 local)
        {
            return local.x >= (PanelW - 52f) * UiScale && local.x <= PanelW * UiScale
                && local.y <= 34f * UiScale;
        }

        // 鼠标锁定跟随速度滑块
        const float AimSliderX = 196f, AimSliderY = 754f, AimSliderW = 300f, AimSliderH = 30f;
        static RectTransform _aimFillRt, _aimKnobRt;
        static bool _aimSliderDrag;

        static void BuildAimSlider()
        {
            MakeImage("AimBarBg", AimSliderX, AimSliderY + 8f, AimSliderW, 14f, new Color(0.08f, 0.10f, 0.14f, 0.92f));
            _aimFillRt = MakeImage("AimFill", AimSliderX, AimSliderY + 8f, 10f, 14f, new Color(0.30f, 0.65f, 1f, 0.85f));
            _aimKnobRt = MakeImage("AimKnob", AimSliderX, AimSliderY + 2f, 14f, 26f, new Color(0.95f, 0.96f, 1f, 1f));
            RefreshAimSlider();
        }

        static void RefreshAimSlider()
        {
            try
            {
                if (_aimFillRt == null || _aimKnobRt == null) return;
                float t = AimLine.FollowSmooth / 0.4f;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                float bx = AimSliderX * UiScale;
                float bw = (AimSliderW - 14f) * UiScale;
                float fy = (AimSliderY + 8f) * UiScale;
                _aimFillRt.anchoredPosition = new Vector2(bx, fy);
                float fw = 7f * UiScale + t * bw;
                if (fw < 2f) fw = 2f;
                _aimFillRt.sizeDelta = new Vector2(fw, 14f * UiScale);
                _aimKnobRt.anchoredPosition = new Vector2(bx + t * bw, (AimSliderY + 2f) * UiScale);
            }
            catch { }
        }

        static void SetAimSmoothFromLocalX(float localX)
        {
            try
            {
                float bx = AimSliderX * UiScale;
                float bw = (AimSliderW - 14f) * UiScale;
                float t = (localX - bx) / bw;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                AimLine.FollowSmooth = (float)Math.Round(t * 0.4f, 2);
                RefreshAimSlider();
                if (_aimSmTxt != null)
                    _aimSmTxt.text = AimLine.FollowSmooth <= 0.002f
                        ? "跟随：立即"
                        : "跟随：慢 " + (AimLine.FollowSmooth * 1000f).ToString("0") + "ms";
            }
            catch { }
        }

        static bool InAimSliderArena(Vector2 local)
        {
            float bx = AimSliderX * UiScale, by = (AimSliderY + 2f) * UiScale;
            float bw = AimSliderW * UiScale, bh = 26f * UiScale;
            return local.x >= bx - 6f && local.x <= bx + bw + 6f && local.y >= by - 6f && local.y <= by + bh + 6f;
        }

        static void ApplyPanelRect()
        {
            if (_panelRt == null) return;
            _panelRt.anchoredPosition = _panelPos;
            _panelRt.sizeDelta = new Vector2(PanelW, PanelH);
            _panelRt.localScale = new Vector3(_panelScale, _panelScale, 1f);
        }

        static bool IsInsidePanel(Vector2 mp)
        {
            return mp.x >= _panelPos.x && mp.x <= _panelPos.x + PanelW * _panelScale
                && mp.y >= _panelPos.y && mp.y <= _panelPos.y + PanelH * _panelScale;
        }

        static Vector2 ToLocal(Vector2 mp)
        {
            return new Vector2((mp.x - _panelPos.x) / _panelScale, (mp.y - _panelPos.y) / _panelScale);
        }

        static bool HitButton(Vector2 local, out BtnDef hit)
        {
            hit = null;
            for (int i = 0; i < _btns.Count; i++)
            {
                var b = _btns[i];
                if (local.x >= b.X && local.x <= b.X + b.W && local.y >= b.Y && local.y <= b.Y + b.H) { hit = b; return true; }
            }
            return false;
        }

        static void ZoomBy(int dir)
        {
            _panelScale += dir * 0.08f;
            if (_panelScale < 0.55f) _panelScale = 0.55f;
            if (_panelScale > 2.2f) _panelScale = 2.2f;
            ClampPanelPos();
            ApplyPanelRect();
        }

        static void ClampPanelPos()
        {
            float maxX = Math.Max(0f, Screen.width - PanelW * _panelScale);
            float maxY = Math.Max(0f, Screen.height - PanelH * _panelScale);
            if (_panelPos.x < 0f) _panelPos.x = 0f;
            if (_panelPos.y < 0f) _panelPos.y = 0f;
            if (_panelPos.x > maxX) _panelPos.x = maxX;
            if (_panelPos.y > maxY) _panelPos.y = maxY;
        }

        static void TickPanel()
        {
            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse == null || _panelRt == null) return;
                Vector2 mp = mouse.position.ReadValue();
                bool down = mouse.leftButton.isPressed;
                Vector2 sc = mouse.scroll.ReadValue();
                if ((sc.y > 1f || sc.y < -1f) && IsInsidePanel(mp)) ZoomBy(sc.y > 0f ? 1 : -1);

                if (MouseLeftDown(out Vector2 mpm))
                {
                    Vector2 lcl = ToLocal(mpm);
                    if (InWSliderArena(lcl))
                    {
                        _wSliderDrag = true;
                        SetWFromLocalX(lcl.x);
                    }
                    else if (InDSliderArena(lcl))
                    {
                        _dSliderDrag = true;
                        SetDFromLocalX(lcl.x);
                    }
                    else if (InSliderArena(lcl))
                    {
                        _sliderDrag = true;
                        SetHzFromLocalX(lcl.x);
                    }
                    else if (InAimSliderArena(lcl))
                    {
                        _aimSliderDrag = true;
                        SetAimSmoothFromLocalX(lcl.x);
                    }
                    else if (InGrip(lcl))
                    {
                        _resizeDrag = true;
                        _gripLastMp = mpm;
                    }
                    else
                    {
                        BtnDef hit;
                        if (HitButton(ToLocal(mpm), out hit))
                        {
                            DoAction(hit.Id);
                        }
                        else if (IsInsidePanel(mpm))
                        {
                            _dragging = true;
                            _dragOff = mpm - _panelPos;
                        }
                    }
                }

                if (_dSliderDrag)
                {
                    if (down) SetDFromLocalX(ToLocal(mp).x);
                    else _dSliderDrag = false;
                }

                if (_wSliderDrag)
                {
                    if (down) SetWFromLocalX(ToLocal(mp).x);
                    else _wSliderDrag = false;
                }

                if (_sliderDrag)
                {
                    if (down) SetHzFromLocalX(ToLocal(mp).x);
                    else _sliderDrag = false;
                }

                if (_aimSliderDrag)
                {
                    if (down) SetAimSmoothFromLocalX(ToLocal(mp).x);
                    else _aimSliderDrag = false;
                }

                if (_resizeDrag)
                {
                    if (down)
                    {
                        _panelScale += (mp.x - _gripLastMp.x) / 260f;
                        if (_panelScale < 0.55f) _panelScale = 0.55f;
                        if (_panelScale > 2.2f) _panelScale = 2.2f;
                        _gripLastMp = mp;
                        ClampPanelPos();
                        ApplyPanelRect();
                    }
                    else _resizeDrag = false;
                }

                if (_dragging)
                {
                    if (down)
                    {
                        _panelPos = mp - _dragOff;
                        ClampPanelPos();
                        ApplyPanelRect();
                    }
                    else _dragging = false;
                }
            }
            catch { }
        }

        static void DoAction(string id)
        {
            switch (id)
            {
                case "log":
                    DumpLog();
                    break;
                case "close":
                    _visible = false;
                    if (_panelGo != null) _panelGo.SetActive(false);
                    break;
                case "zoomIn":
                    ZoomBy(1);
                    break;
                case "zoomOut":
                    ZoomBy(-1);
                    break;
                case "save":
                    MenuState.Save();
                    LastAction = "菜单状态已保存";
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][状态] 已保存到 MenuState.conf");
                    break;
                case "savetodisk":
                    SaveProgress();
                    break;
                case "sp10": SetSpeed(1f); break;
                case "sp12": SetSpeed(1.2f); break;
                case "sp14": SetSpeed(1.4f); break;
                case "sp16": SetSpeed(1.6f); break;
                case "sp18": SetSpeed(1.8f); break;
                case "sp20": SetSpeed(2f); break;
                case "autoDodge":
                    Tracker.AutoDodgeOn = !Tracker.AutoDodgeOn;
                    LastAction = Tracker.AutoDodgeOn ? "自动闪避已开" : "自动闪避已关";
                    RefreshCheats();
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][开关] 自动闪避 " + (Tracker.AutoDodgeOn ? "开" : "关"));
                    break;
                case "autoQte":
                    AutoQTE.On = !AutoQTE.On;
                    LastAction = AutoQTE.On ? "自动QTE已开" : "自动QTE已关";
                    RefreshCheats();
                    AutoDodgePlugin.L?.LogInfo("[自动QTE][开关] " + (AutoQTE.On ? "开" : "关"));
                    break;
                case "langZh": SetLang(0); break;
                case "langEn": SetLang(1); break;
                case "fresh":
                    Cheats.FreshFood = !Cheats.FreshFood;
                    LastAction = Cheats.FreshFood ? "食物保鲜已开" : "食物保鲜已关";
                    RefreshCheats();
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][作弊] 食物保鲜 " + (Cheats.FreshFood ? "开" : "关"));
                    break;
                case "aim":
                    Cheats.AimLine = !Cheats.AimLine;
                    LastAction = Cheats.AimLine ? "瞄准线已开" : "瞄准线已关";
                    RefreshCheats();
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][瞄准线] " + (Cheats.AimLine ? "开" : "关"));
                    break;
                case "aimU":
                    AimLine.Nudge(0f, 10f);
                    RefreshCheats();
                    break;
                case "aimD":
                    AimLine.Nudge(0f, -10f);
                    RefreshCheats();
                    break;
                case "aimL":
                    AimLine.Nudge(-10f, 0f);
                    RefreshCheats();
                    break;
                case "aimR":
                    AimLine.Nudge(10f, 0f);
                    RefreshCheats();
                    break;
                case "aimReset":
                    AimLine.ResetOffset();
                    RefreshCheats();
                    LastAction = "瞄准线起点已归零";
                    break;
                case "aimBox":
                    AimLine.BoxOn = !AimLine.BoxOn;
                    LastAction = AimLine.BoxOn ? "锁定框已显示" : "锁定框已隐藏";
                    RefreshCheats();
                    break;
                case "aggT1": AimLine.NudgeBox(0, 20f); RefreshCheats(); break;
                case "aggT2": AimLine.NudgeBox(0, -20f); RefreshCheats(); break;
                case "aggB1": AimLine.NudgeBox(1, 20f); RefreshCheats(); break;
                case "aggB2": AimLine.NudgeBox(1, -20f); RefreshCheats(); break;
                case "aggL1": AimLine.NudgeBox(2, 20f); RefreshCheats(); break;
                case "aggL2": AimLine.NudgeBox(2, -20f); RefreshCheats(); break;
                case "aggR1": AimLine.NudgeBox(3, 20f); RefreshCheats(); break;
                case "aggR2": AimLine.NudgeBox(3, -20f); RefreshCheats(); break;
                case "aggReset": AimLine.ResetBox(); RefreshCheats(); LastAction = "锁定框已重置"; break;
                case "dbgTags":
                    DebugTags.On = !DebugTags.On;
                    LastAction = DebugTags.On ? "实体标记已开" : "实体标记已关";
                    RefreshCheats();
                    break;
                case "hpTags":
                    HealthTags.On = !HealthTags.On;
                    LastAction = HealthTags.On ? "僵尸血量已开" : "僵尸血量已关";
                    RefreshCheats();
                    break;
                case "aimHead":
                    AimLine.AimPart = 0; RefreshCheats(); LastAction = "锁定部位：头"; break;
                case "aimChest":
                    AimLine.AimPart = 1; RefreshCheats(); LastAction = "锁定部位：胸"; break;
                case "aimLegs":
                    AimLine.AimPart = 2; RefreshCheats(); LastAction = "锁定部位：腿"; break;
                case "aimLock":
                    Cheats.AimLock = !Cheats.AimLock;
                    LastAction = Cheats.AimLock ? "鼠标锁定已开" : "鼠标锁定已关";
                    RefreshCheats();
                    break;
                case "outline":
                    ZombieOutline.On = !ZombieOutline.On;
                    ZombieOutline.ForceRefresh();
                    LastAction = ZombieOutline.On ? "僵尸方框已开" : "僵尸方框已关";
                    RefreshCheats();
                    break;
                case "bonesOn":
                    ZombieOutline.BonesOn = !ZombieOutline.BonesOn;
                    ZombieOutline.ForceRefresh();
                    LastAction = ZombieOutline.BonesOn ? "骨架已开" : "骨架已关";
                    RefreshCheats();
                    break;
                case "dirOn":
                    ZombieOutline.DirOn = !ZombieOutline.DirOn;
                    ZombieOutline.ForceRefresh();
                    LastAction = ZombieOutline.DirOn ? "朝向已开" : "朝向已关";
                    RefreshCheats();
                    break;
                case "fpsUp":
                    ZombieOutline.Hz = Mathf.Min(60f, ZombieOutline.Hz + 5f);
                    ZombieOutline.ForceRefresh();
                    LastAction = "描边刷新率 " + ZombieOutline.Hz.ToString("0") + "/秒";
                    RefreshCheats();
                    break;
                case "fpsDown":
                    ZombieOutline.Hz = Mathf.Max(1f, ZombieOutline.Hz - 5f);
                    ZombieOutline.ForceRefresh();
                    LastAction = "描边刷新率 " + ZombieOutline.Hz.ToString("0") + "/秒";
                    RefreshCheats();
                    break;
                case "outW1":
                    ZombieOutline.Width = Mathf.Min(12f, ZombieOutline.Width + 1f);
                    ZombieOutline.ForceRefresh();
                    LastAction = "描边粗细 " + ZombieOutline.Width.ToString("0") + "px";
                    RefreshCheats();
                    break;
                case "outW2":
                    ZombieOutline.Width = Mathf.Max(1f, ZombieOutline.Width - 1f);
                    ZombieOutline.ForceRefresh();
                    LastAction = "描边粗细 " + ZombieOutline.Width.ToString("0") + "px";
                    RefreshCheats();
                    break;
            }
        }

        static void RefreshCheats()
        {
            try
            {
                if (_godTxt != null) _godTxt.text = Cheats.God ? T("无限生命：开") : T("无限生命：关");
                if (_staTxt != null) _staTxt.text = Cheats.InfiniteStamina ? T("无限耐力：开") : T("无限耐力：关");
                if (_hungTxt != null) _hungTxt.text = Cheats.NoHunger ? T("无饥饿：开") : T("无饥饿：关");
                if (_ammoTxt != null) _ammoTxt.text = Cheats.InfiniteAmmo ? T("无限弹药：开") : T("无限弹药：关");
                if (_dmgTxt != null) _dmgTxt.text = T("伤害：") + Cheats.DamageName;
                if (_freshTxt != null) _freshTxt.text = Cheats.FreshFood ? T("食物保鲜：开") : T("食物保鲜：关");
                if (_aimTxt != null) _aimTxt.text = Cheats.AimLine ? T("瞄准线：开") : T("瞄准线：关");
                if (_aimOffTxt != null) _aimOffTxt.text = T("归零 ") + AimLine.OffX.ToString("0") + "," + AimLine.OffY.ToString("0");
                if (_aggTxt != null) _aggTxt.text = AimLine.BoxOn ? T("锁定框：开") : T("锁定框：关");
                if (_aggInfoTxt != null) _aggInfoTxt.text = T("框 L") + AimLine.BoxL.ToString("0") + " R" + AimLine.BoxR.ToString("0")
                    + " T" + AimLine.BoxT.ToString("0") + " B" + AimLine.BoxB.ToString("0");
                if (_dbgTxt != null) _dbgTxt.text = DebugTags.On ? T("实体标记：开") : T("实体标记：关");
                if (_hpTxt != null) _hpTxt.text = HealthTags.On ? T("僵尸血量：开") : T("僵尸血量：关");
                // 部位由三个独立按钮的高亮表示，不再合并到一个文本里
                if (_lockTxt != null) _lockTxt.text = Cheats.AimLock ? T("鼠标锁定：开") : T("鼠标锁定：关");
                if (_outlineTxt != null) _outlineTxt.text = ZombieOutline.On
                    ? T("方框：开")
                    : T("方框：关");
                if (_boneTxt != null) _boneTxt.text = ZombieOutline.BonesOn ? T("骨架：开") : T("骨架：关");
                if (_dirTxt != null) _dirTxt.text = ZombieOutline.DirOn ? T("朝向：开") : T("朝向：关");
                if (_wTxt != null) _wTxt.text = T("线宽：") + ZombieOutline.Width.ToString("0") + "px";
                if (_autoTxt != null) _autoTxt.text = Tracker.AutoDodgeOn ? T("自动闪避：开") : T("自动闪避：关");
                if (_qteTxt != null) _qteTxt.text = AutoQTE.On ? T("自动QTE：开") : T("自动QTE：关");
                if (_dTxt != null) _dTxt.text = T("闪避延迟：") + Tracker.DelayMs.ToString("0") + "ms";
                if (_fpsTxt != null) _fpsTxt.text = T("刷新率：") + ZombieOutline.Hz.ToString("0") + T("/秒");
                for (int i = 0; i < _btns.Count; i++)
                {
                    var b = _btns[i];
                    if (b.Img == null) continue;
                    if (b.Id == "god") b.Img.color = Cheats.God ? ColChOn : ColCh;
                    else if (b.Id == "sta") b.Img.color = Cheats.InfiniteStamina ? ColChOn : ColCh;
                    else if (b.Id == "hung") b.Img.color = Cheats.NoHunger ? ColChOn : ColCh;
                    else if (b.Id == "ammo") b.Img.color = Cheats.InfiniteAmmo ? ColChOn : ColCh;
                    else if (b.Id == "dmg") b.Img.color = Cheats.DamageMode > 0 ? ColChOn : ColCh;
                    else if (b.Id == "fresh") b.Img.color = Cheats.FreshFood ? ColChOn : ColCh;
                    else if (b.Id == "aim") b.Img.color = Cheats.AimLine ? ColChOn : ColCh;
                    else if (b.Id == "aimHead") b.Img.color = (AimLine.AimPart == 0) ? ColChOn : ColCh;
                    else if (b.Id == "aimChest") b.Img.color = (AimLine.AimPart == 1) ? ColChOn : ColCh;
                    else if (b.Id == "aimLegs") b.Img.color = (AimLine.AimPart == 2) ? ColChOn : ColCh;
                    else if (b.Id == "aimLock") b.Img.color = Cheats.AimLock ? ColChOn : ColCh;
                    else if (b.Id == "outline") b.Img.color = ZombieOutline.On ? ColChOn : ColCh;
                    else if (b.Id == "bonesOn") b.Img.color = ZombieOutline.BonesOn ? ColChOn : ColCh;
                    else if (b.Id == "dirOn") b.Img.color = ZombieOutline.DirOn ? ColChOn : ColCh;
                    else if (b.Id == "autoDodge") b.Img.color = Tracker.AutoDodgeOn ? ColChOn : ColCh;
                    else if (b.Id == "autoQte") b.Img.color = AutoQTE.On ? ColChOn : ColCh;
                }
            }
            catch { }
        }

        static void SetSpeed(float f)
        {
            SpeedFactor = f;
            LastAction = T("倍速 ") + f.ToString("0.0") + "x";
            RefreshSpeedButtons();
            AutoDodgePlugin.L?.LogInfo(T("[自动闪避][倍速] -> ") + f.ToString("0.0") + "x");
        }

        static void RefreshSpeedButtons()
        {
            for (int i = 0; i < _btns.Count; i++)
            {
                var b = _btns[i];
                if (b.Txt == null || b.Img == null) continue;
                if (!b.Id.StartsWith("sp") || b.Id.Length < 4) continue;
                float f = int.Parse(b.Id.Substring(2)) / 10f;
                bool on = Math.Abs(SpeedFactor - f) < 0.01f;
                b.Img.color = on ? ColSpeedOn : ColSpeed;
                b.Txt.color = on ? Color.white : new Color(0.85f, 0.88f, 0.95f);
            }
        }

        static void RefreshInfButton()
        {
            for (int i = 0; i < _btns.Count; i++)
            {
                var b = _btns[i];
                if (b.Id != "inf" || b.Txt == null || b.Img == null) continue;
                b.Img.color = Tracker.InfiniteDodge ? ColInfOn : ColInf;
                b.Txt.text = Tracker.InfiniteDodge ? "无限闪避：开" : "无限闪避：关";
            }
        }

        // 每帧维持时间缩放；暂停（timeScale 接近 0）时不介入，避免卡死暂停菜单
        public static void ApplySpeed()
        {
            try
            {
                float cur = Time.timeScale;
                float want = SpeedFactor <= 0.001f ? 1f : SpeedFactor;
                if (cur > 0.05f)
                {
                    if (want > 1.001f)
                    {
                        if (Math.Abs(cur - want) > 0.02f) Time.timeScale = want;
                        _speedApplied = want;
                    }
                    else if (_speedApplied > 1.001f)
                    {
                        if (Math.Abs(cur - _speedApplied) < 0.25f) Time.timeScale = 1f;
                        _speedApplied = 1f;
                    }
                }
            }
            catch { }
        }

        static string BuildText()
        {
            var cam = Camera.main;
            string camInfo = cam == null ? "none" : cam.transform.position.ToString();
            return "ITD 自动闪避 v1.0　　（F1 开关菜单）\n"
                 + "\n威胁记录　： " + Tracker.Threats.Count
                 + "\n触发闪避　： " + Tracker.ForceDodges
                 + "\n输入调用　： " + Tracker.InputCalls
                 + "\n状态机调用： " + Tracker.StateSimCalls
                 + "\n已见状态　： " + Tracker.SeenStates.Count
                 + "\n心跳　　　： " + Tracker.Heartbeats
                 + "\n当前倍速　： " + SpeedFactor.ToString("0.0") + "x"
                 + "\n相机位置　： " + camInfo
                 + "\n最近动作　： " + LastAction
                 + "\n\n点「导出日志」写到 BepInEx\\AutoDodge_Debug.log"
                 + "\n\n作者：Sherry1250"
                 + "\n支持者：船到桥头自然沉，SK，GYW，CTQ";
        }

        public static void DumpLog()
        {
            try
            {
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== 自动闪避诊断 " + now + " ===");
                sb.AppendLine("威胁记录: " + Tracker.Threats.Count);
                sb.AppendLine("触发闪避: " + Tracker.ForceDodges);
                sb.AppendLine("输入调用: " + Tracker.InputCalls);
                sb.AppendLine("闪避Sim: " + Tracker.DodgeSimCalls);
                sb.AppendLine("玩家坐标: " + (Tracker.HasPlayer ? Tracker.PlayerPos.ToString() : "未锁定"));
                sb.AppendLine("判定窗口: " + Tracker.MinDist + " ~ " + Tracker.MaxDist + " m / " + Tracker.MaxAge + " s");
                var cam = Camera.main;
                sb.AppendLine("相机: " + (cam == null ? "null" : cam.transform.position.ToString()));
                sb.AppendLine("已见状态类型: " + Tracker.SeenStates.Count);
                foreach (var s in Tracker.SeenStates) sb.AppendLine("  - " + s);
                foreach (var th in Tracker.Threats)
                {
                    string d = Tracker.HasPlayer
                        ? Vector3.Distance(th.Pos, Tracker.PlayerPos).ToString("0.00")
                        : "?";
                    sb.AppendLine("  威胁 @" + th.Pos + " 距玩家=" + d + "m t=" + th.Time.ToString("0.00"));
                }
                var dir = Paths.BepInExRootPath;
                var path = System.IO.Path.Combine(dir, "AutoDodge_Debug_" + stamp + ".log");
                File.AppendAllText(path, sb.ToString() + Environment.NewLine);
                try { File.AppendAllText(System.IO.Path.Combine(dir, "AutoDodge_Debug.log"), sb.ToString() + Environment.NewLine); } catch { }
                LastAction = "saved " + System.IO.Path.GetFileName(path);
                AutoDodgePlugin.L?.LogInfo("[自动闪避][导出] 已写入 " + path);
            }
            catch (Exception e)
            {
                LastAction = "export failed: " + e.Message;
                AutoDodgePlugin.L?.LogError("[自动闪避][导出] " + e);
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStateMachine), nameof(CharacterStateMachine.OnNewStateEntered))]
    public static class Patch_NewStateEntered
    {
        static void Postfix(CharacterStateMachine __instance, ICharacterState __0)
        {
            try { Tracker.OnState(__instance, __0); }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避] OnNewStateEntered ex " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(CharacterStateMachine), nameof(CharacterStateMachine.OnChildStateChanged))]
    public static class Patch_ChildStateChanged
    {
        static void Postfix(CharacterStateMachine __instance, ICharacterState __0, ICharacterState __1)
        {
            try { Tracker.OnState(__instance, __1); }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避] OnChildStateChanged ex " + e.Message); }
        }
    }

    [HarmonyPatch]
    public static class Patch_PlayerInputs
    {
        static MethodBase TargetMethod()
        {
            var t = typeof(PlayerInputProvider);
            return t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.EndsWith("GetInputs", StringComparison.Ordinal)
                                      && m.GetParameters().Length == 1);
        }

        static void Postfix(PlayerInputProvider __instance, ref CharacterInputs __0)
        {
            try
            {
                Tracker.InputCalls++;
                Tracker.NotePlayerProvider(__instance);
                Tracker.BindPlayerSm();
                UI.Tick();
                if (Tracker.ShouldDodge())
                {
                    __0.DodgeDown = true;
                }
                // 探针：观察游戏真实输入（手动按闪避时应能看到 DodgeDown=True）
                Tracker.ProbeInputs(__0);
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogInfo("[自动闪避] GetInputs ex " + e.Message);
            }
        }
    }

    // 观察闪避系统本身是否跑起来（辅助诊断）
    [HarmonyPatch]
    public static class Patch_DodgeSystemOld
    {
        static MethodBase TargetMethod()
        {
            return typeof(DRS.Game.Character.Systems.Dodge.CharacterDodgeSystem)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "Simulate");
        }

        static void Postfix()
        {
            Tracker.DodgeSimCalls++;
        }
    }

    // 绑定玩家角色：角色被设置输入提供者时，记录 root 与 provider 的配对
    [HarmonyPatch(typeof(DRS.Game.Character.CharacterRoot), nameof(DRS.Game.Character.CharacterRoot.SetInputProvider))]
    public static class Patch_SetInputProvider
    {
        static void Postfix(DRS.Game.Character.CharacterRoot __instance, DRS.Game.Input.ICharacterInputProvider __0)
        {
            try { Tracker.TryBindPlayer(__instance, __0); }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避] SetInputProvider ex " + e.Message); }
        }
    }

    // 玩家角色每帧刷新位置
    [HarmonyPatch(typeof(DRS.Game.Character.CharacterRoot), nameof(DRS.Game.Character.CharacterRoot.Simulate))]
    public static class Patch_RootSim
    {
        static void Postfix(DRS.Game.Character.CharacterRoot __instance)
        {
            try
            {
                Tracker.NoteRoot(__instance);
                if (Tracker.IsPlayerRoot(__instance))
                {
                    var tr = __instance.CharacterTransform;
                    if (tr != null) Tracker.SetPlayerPos(tr.position);
                }
            }
            catch { }
        }
    }

    // 刺杀：游戏默认只允许从背后刺杀。按下互动键的瞬间把最近的僵尸临时转向面对你，
    // 让判定认为「你在它背后」，判定通过后再把朝向还原。
    [HarmonyPatch(typeof(DRS.Game.Character.Systems.ExecutionSystem.CharacterExecutionSystem), "DRS_Game_Character_Systems_ICharacterSystem_Simulate")]
    public static class Patch_ExecuteAnyAngle
    {
        static IntPtr _tgt = IntPtr.Zero;
        static UnityEngine.Quaternion _orig;
        static float _restoreAt = -1f;
        public static bool Enabled = true;
        static bool _lastInteract;

        static void Prefix(DRS.Game.Character.Systems.ExecutionSystem.CharacterExecutionSystem __instance, ref CharacterInputs __1)
        {
            try
            {
                float now = Time.time;
                // 到点还原朝向
                if (_tgt != IntPtr.Zero && now >= _restoreAt)
                {
                    var rr = Tracker.GetRoot(_tgt);
                    if (rr != null) { try { rr.CharacterTransform.rotation = _orig; } catch { } }
                    _tgt = IntPtr.Zero; _restoreAt = -1f;
                }
                if (!Enabled) { _lastInteract = __1.InteractDown; return; }

                // 心跳：确认这个函数的补丁真的在被调用
                if (now - _hb > 1.0f)
                {
                    _hb = now;
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][刺杀] 心跳 在跑 InteractDown=" + __1.InteractDown
                        + " Held=" + __1.InteractHeld + " root=" + (PlayerOf(__instance) != null ? "有" : "无")
                        + " 角色数=" + Tracker.RootPos.Count);
                }

                // 探针：任何互动类按键按下时打一条，便于确认字段
                if ((__1.InteractDown || __1.InteractHeld || __1.GrabIsDown || __1.ShoveDown) && now - _lastLog > 0.35f)
                {
                    _lastLog = now;
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][刺杀] 输入 InteractDown=" + __1.InteractDown
                        + " Held=" + __1.InteractHeld + " Grab=" + __1.GrabIsDown + " Shove=" + __1.ShoveDown
                        + " 附近=" + NearestInfo(__instance));
                }

                bool down = __1.InteractDown;
                bool rising = down && !_lastInteract;
                _lastInteract = down;
                if (!rising || _tgt != IntPtr.Zero) return;

                var root = PlayerOf(__instance);
                if (root == null) { AutoDodgePlugin.L?.LogInfo("[自动闪避][刺杀] 未拿到执行者 root"); return; }
                var tr = root.CharacterTransform;
                if (tr == null) return;
                var me = tr.position;
                IntPtr best = IntPtr.Zero;
                float bestD = 2.4f;
                foreach (var kv in Tracker.RootPos)
                {
                    if (kv.Key == Tracker.PtrOf(root)) continue;
                    var p = kv.Value;
                    float dx = p.x - me.x, dy = p.y - me.y;
                    float d = (float)System.Math.Sqrt(dx * dx + dy * dy);
                    if (d < bestD) { bestD = d; best = kv.Key; }
                }
                if (best == IntPtr.Zero) { AutoDodgePlugin.L?.LogInfo("[自动闪避][刺杀] 附近没有 2.4m 内角色"); return; }
                var zroot = Tracker.GetRoot(best);
                if (zroot == null) return;
                var ztr = zroot.CharacterTransform;
                if (ztr == null) return;
                _orig = ztr.rotation;
                ztr.rotation = _orig * UnityEngine.Quaternion.Euler(0f, 0f, 180f);
                _tgt = best;
                _restoreAt = now + 1.4f;
                AutoDodgePlugin.L?.LogInfo("[自动闪避][刺杀] 已把僵尸转身，距离=" + bestD.ToString("0.00") + " 持续1.4s");
            }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避][刺杀] ex " + e.Message); }
        }

        static float _lastLog = -100f;
        static float _hb = -100f;

        static DRS.Game.Character.CharacterRoot PlayerOf(DRS.Game.Character.Systems.ExecutionSystem.CharacterExecutionSystem sys)
        {
            try
            {
                var sm = sys.m_owner;
                if (sm == null) return null;
                return sm.Root;
            }
            catch { return null; }
        }

        static string NearestInfo(DRS.Game.Character.Systems.ExecutionSystem.CharacterExecutionSystem sys)
        {
            try
            {
                var root = PlayerOf(sys);
                if (root == null) return "无root";
                var tr = root.CharacterTransform;
                if (tr == null) return "无tr";
                var me = tr.position;
                float best = float.MaxValue;
                string bn = "-";
                foreach (var kv in Tracker.RootPos)
                {
                    float dx = kv.Value.x - me.x, dy = kv.Value.y - me.y;
                    float d = (float)System.Math.Sqrt(dx * dx + dy * dy);
                    if (d < best) { best = d; bn = d.ToString("0.00"); }
                }
                return "最近" + bn + " 角色数" + Tracker.RootPos.Count;
            }
            catch { return "err"; }
        }
    }

    // QTE（被抓时的小游戏）：出现 0.5 秒后自动判成功
    [HarmonyPatch(typeof(DRS.Game.QTE.QTEController), nameof(DRS.Game.QTE.QTEController.Simulate))]
    public static class Patch_AutoQte
    {
        static float _qteStart = -1f;
        static bool _logged;

        static void Postfix(DRS.Game.QTE.QTEController __instance)
        {
            try
            {
                if (!AutoQTE.On) return;   // 菜单里"自动QTE"关着就完全不管 QTE
                if (__instance == null || !__instance.IsActive)
                {
                    if (_qteStart > 0f) { AutoDodgePlugin.L?.LogInfo("[自动闪避][QTE] 结束/已退出"); _qteStart = -1f; _logged = false; }
                    return;
                }
                float now = Time.time;
                if (_qteStart < 0f)
                {
                    _qteStart = now;
                    if (!_logged) { _logged = true; AutoDodgePlugin.L?.LogInfo("[自动闪避][QTE] 出现，0.01s 后自动完成"); }
                    return;
                }
                if (now - _qteStart >= 0.01f)
                {
                    __instance.DisableQTE(false, DRS.Static.QTEResult.Success);
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][QTE] 0.01s 到，已自动判成功");
                    _qteStart = -1f;
                    _logged = false;
                }
            }
            catch (Exception e) { AutoDodgePlugin.L?.LogInfo("[自动闪避][QTE] ex " + e.Message); }
        }
    }

    // 闪避系统自身的每帧入口：在这里改输入，游戏立刻就读到
    [HarmonyPatch]
    public static class Patch_DodgeSystem
    {
        static MethodBase TargetMethod()
        {
            var t = typeof(DRS.Game.Character.Systems.Dodge.CharacterDodgeSystem);
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (m.Name.Contains("Simulate"))
                {
                    AutoDodgePlugin.L?.LogInfo("[自动闪避] DodgeSystem 目标: " + m.Name + " 参数数=" + m.GetParameters().Length);
                    return m;
                }
            }
            return null;
        }

        static void Prefix(ref DRS.Game.Input.CharacterInputs __1)
        {
            try
            {
                Tracker.DodgeSimCalls++;
                if (Tracker.Verbose && Tracker.DodgeSimCalls % 120 == 1)
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][探针] DodgeSystem 被调用 #" + Tracker.DodgeSimCalls + " DodgeDown=" + __1.DodgeDown);
                if (Tracker.IsThreatNear())
                {
                    __1.DodgeDown = true;
                    if (Tracker.Verbose) AutoDodgePlugin.L?.LogInfo("[自动闪避][注入] DodgeSystem 内写入 DodgeDown");
                }
            }
            catch { }
        }
    }


    // 无限闪避：闪避条（体力）用掉后立刻回满，不再受充能限制
    [HarmonyPatch(typeof(DRS.Game.Character.Status.Stamina), "Simulate")]
    public static class Patch_Stamina
    {
        static void Postfix(DRS.Game.Character.Status.Stamina __instance)
        {
            if (!Tracker.InfiniteDodge) return;
            try
            {
                var cur = __instance.m_currentStamina;
                if (cur == null) return;
                cur.SetToMaxValue();
            }
            catch { }
        }
    }

    // 探针：观察游戏自己在手动闪避时如何设置闪避标志
    [HarmonyPatch(typeof(DRS.Game.Character.States.MotorControls), "SetReadyToDodge")]
    public static class Patch_ProbeSetReady
    {
        static void Postfix(bool __0, bool __1)
        {
            if (Tracker.Verbose) AutoDodgePlugin.L?.LogInfo("[自动闪避][探针] SetReadyToDodge(" + __0 + "," + __1 + ")");
        }
    }

    [HarmonyPatch(typeof(DRS.Game.Character.States.MotorControls), "get_ReadyToDodge")]
    public static class Patch_ProbeReadyGet
    {
        static void Postfix(ref bool __result)
        {
            if (__result && Tracker.Verbose) AutoDodgePlugin.L?.LogInfo("[自动闪避][探针] ReadyToDodge 读取=True");
        }
    }

    [HarmonyPatch(typeof(DRS.Game.Character.States.ConcreteStates.Dodge.DodgeState), "OnEnter")]
    public static class Patch_ProbeDodgeEnter
    {
        static void Postfix()
        {
            if (Tracker.Verbose) AutoDodgePlugin.L?.LogInfo("[自动闪避][探针] 进入 DodgeState");
        }
    }

    // 玩家伤害拦截（真实方法名为接口实现形式）
    [HarmonyPatch(typeof(DRS.Game.Character.CharacterRoot), "DRS_Game_Damage_IDamageReceiver_Damage")]
    public static class Patch_PlayerDamage
    {
        static bool Prefix(DRS.Game.Character.CharacterRoot __instance)
        {
            try
            {
                if (!Tracker.IsPlayerRoot(__instance)) return true;
                Tracker.LogBlock();
                return false;
            }
            catch { return true; }
        }
    }

    // 免疫查询：让玩家永远被判为免伤
    [HarmonyPatch(typeof(DRS.Game.Character.CharacterRoot), "DRS_Game_Damage_IDamageImmunityProvider_IsDamageImmune")]
    public static class Patch_PlayerImmune
    {
        static void Postfix(DRS.Game.Character.CharacterRoot __instance, ref bool __result)
        {
            try
            {
                if (Tracker.IsPlayerRoot(__instance)) { __result = true; Tracker.LogBlock(); }
            }
            catch { }
        }
    }

    // 状态机每帧驱动：UI + 自动闪避注入
    [HarmonyPatch(typeof(CharacterStateMachine), nameof(CharacterStateMachine.Simulate))]
    public static class Patch_Simulate
    {
        static void Prefix(CharacterStateMachine __instance, ref CharacterInputs __1)
        {
            try
            {
                Tracker.NoteCallerSm(__instance);
                // 闪避注入必须在状态机处理输入之前完成（Prefix），Postfix 时代码已消费过输入
                if (Tracker.IsPlayer(__instance))
                {
                    Tracker.UpdatePosFrom(__instance);
                    Tracker.ProbeMotor(__instance);
                    if (Tracker.AutoDodgeOn && Tracker.ShouldDodge())
                    {
                        // 与游戏手动闪避完全一致：先置闪避就绪(True, False)，再置 DodgeDown
                        try
                        {
                            __instance.MotorControls.SetReadyToDodge(true, false);
                            if (Tracker.Verbose) AutoDodgePlugin.L?.LogInfo("[自动闪避][注入] 已设闪避就绪 + DodgeDown");
                        }
                        catch (Exception e1) { AutoDodgePlugin.L?.LogInfo("[自动闪避] SetReadyToDodge ex " + e1.Message); }
                        __1.DodgeDown = true;
                    }
                }
            }
            catch { }
        }

        static void Postfix(CharacterStateMachine __instance, ref CharacterInputs __1)
        {
            try
            {
                UI.Tick();
                Tracker.StateSimCalls++;
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogInfo("[自动闪避] Simulate ex " + e.Message);
            }
        }
    }

    // 引擎层心跳：Time.deltaTime 每帧必被游戏读取，用它驱动 UI
    [HarmonyPatch(typeof(Time), "get_deltaTime")]
    public static class Patch_Heartbeat
    {
        static float _lastBeat = -999f;

        static void Postfix()
        {
            try
            {
                Tracker.Heartbeats++;
                UI.ApplySpeed();
                AimLine.Tick();
                DebugTags.Tick();
                HealthTags.Tick();
                ZombieOutline.Tick();
                AutoQTE.Tick();
                if (Tracker.Heartbeats == 1)
                {
                    AutoDodgePlugin.L?.LogInfo("[自动闪避][心跳] Time.deltaTime 钩子已触发");
                }
                UI.Tick();
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogError("[自动闪避][心跳] " + e);
            }
        }
    }

    // ===== 作弊模块（WeMod 功能在游戏本体的实现） =====
    internal static class Cheats
    {
        public static bool God;              // 无限生命
        public static bool InfiniteStamina;  // 无限耐力
        public static bool NoHunger;         // 无饥饿
        public static bool InfiniteAmmo;     // 无限弹药
        public static bool FreshFood;        // 食物不变质
        public static bool AimLine = true;   // 枪口→鼠标红色瞄准线
        public static bool AimLock = true;   // 按住右键时鼠标自动锁定僵尸

        public static int DamageMode;        // 0=原版 1=五倍 2=一击必杀

        public static string DamageName
        {
            get { return DamageMode == 2 ? "一击必杀" : (DamageMode == 1 ? "五倍" : "关"); }
        }
    }

    // ===== 自动 QTE 开关（真正的判定在 Patch_AutoQte 里，这里只持有开关状态） =====
    internal static class AutoQTE
    {
        public static bool On;

        public static void NoteState(string name) { }

        public static void Tick() { }
    }

    // 菜单状态持久化：点「保存状态」写入，启动时自动恢复
    internal static class MenuState
    {
        static string PathCfg
        {
            get { return System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "MenuState.conf"); }
        }

        public static void Load()
        {
            try
            {
                var p = PathCfg;
                if (!System.IO.File.Exists(p)) return;
                foreach (var raw in System.IO.File.ReadAllLines(p, System.Text.Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var v = line.Substring(eq + 1).Trim().ToLowerInvariant();
                    float f;
                    if (!float.TryParse(v, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out f)) continue;
                    bool b = f > 0.5f;
                    switch (k)
                    {
                        case "god": Cheats.God = false; break;                 // 已按主人要求移除，恒关
                        case "stamina": Cheats.InfiniteStamina = false; break;  // 已移除
                        case "hunger": Cheats.NoHunger = false; break;          // 已移除
                        case "ammo": Cheats.InfiniteAmmo = false; break;        // 已移除
                        case "fresh": Cheats.FreshFood = b; break;
                        case "damage": Cheats.DamageMode = 0; break;            // 已移除
                        case "infdodge": Tracker.InfiniteDodge = false; break;   // 已移除
                        case "speed": UI.SpeedFactor = f <= 0f ? 1f : f; break;
                        case "aimline": Cheats.AimLine = b; break;
                        case "aimx": AimLine.OffX = f; break;
                        case "aimy": AimLine.OffY = f; break;
                        case "aggbox": AimLine.BoxOn = b; break;
                        case "aggl": AimLine.BoxL = f; break;
                        case "aggr": AimLine.BoxR = f; break;
                        case "aggt": AimLine.BoxT = f; break;
                        case "aggb": AimLine.BoxB = f; break;
                        case "dbgtags": DebugTags.On = b; break;
                        case "hptags": HealthTags.On = b; break;
                        case "aimpart": AimLine.AimPart = (int)f; break;
                        case "aimlock": Cheats.AimLock = b; break;
                        case "outline": ZombieOutline.On = b; break;
                        case "outwidth": ZombieOutline.Width = Mathf.Clamp(f, 1f, 12f); break;
                        case "boneson": ZombieOutline.BonesOn = b; break;
                        case "diron": ZombieOutline.DirOn = b; break;
                        case "outfps": ZombieOutline.Hz = Mathf.Clamp(f, 1f, 60f); break;
                        case "aimsmooth": AimLine.FollowSmooth = Mathf.Clamp(f, 0f, 0.4f); break;
                        case "autododge": Tracker.AutoDodgeOn = b; break;
                        case "autoqte": AutoQTE.On = b; break;
                        case "lang": UI.Lang = (int)f; break;
                        case "dodgedelay": Tracker.DelayMs = Mathf.Clamp(f, 0f, 900f); Tracker.WindUpDelay = Tracker.DelayMs / 1000f; break;
                    }
                }
                AutoDodgePlugin.L?.LogInfo("[自动闪避][状态] 已恢复菜单状态");
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogError("[自动闪避][状态] 读取失败: " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# F1 菜单状态。点菜单里的「保存状态」写入，下次启动自动恢复。");
                sb.AppendLine("# 1=开 0=关；damage: 0=关 1=五倍 2=一击必杀；speed: 倍速");
                sb.AppendLine("god=" + (Cheats.God ? 1 : 0));
                sb.AppendLine("stamina=" + (Cheats.InfiniteStamina ? 1 : 0));
                sb.AppendLine("hunger=" + (Cheats.NoHunger ? 1 : 0));
                sb.AppendLine("ammo=" + (Cheats.InfiniteAmmo ? 1 : 0));
                sb.AppendLine("fresh=" + (Cheats.FreshFood ? 1 : 0));
                sb.AppendLine("damage=" + Cheats.DamageMode);
                sb.AppendLine("infdodge=" + (Tracker.InfiniteDodge ? 1 : 0));
                sb.AppendLine("speed=" + UI.SpeedFactor.ToString("0.00"));
                sb.AppendLine("aimline=" + (Cheats.AimLine ? 1 : 0));
                sb.AppendLine("aimx=" + AimLine.OffX.ToString("0"));
                sb.AppendLine("aimy=" + AimLine.OffY.ToString("0"));
                sb.AppendLine("aggbox=" + (AimLine.BoxOn ? 1 : 0));
                sb.AppendLine("aggl=" + AimLine.BoxL.ToString("0"));
                sb.AppendLine("aggr=" + AimLine.BoxR.ToString("0"));
                sb.AppendLine("aggt=" + AimLine.BoxT.ToString("0"));
                sb.AppendLine("aggb=" + AimLine.BoxB.ToString("0"));
                sb.AppendLine("dbgtags=" + (DebugTags.On ? 1 : 0));
                sb.AppendLine("hptags=" + (HealthTags.On ? 1 : 0));
                sb.AppendLine("aimpart=" + AimLine.AimPart);
                sb.AppendLine("aimlock=" + (Cheats.AimLock ? 1 : 0));
                sb.AppendLine("outline=" + (ZombieOutline.On ? 1 : 0));
                sb.AppendLine("outwidth=" + ZombieOutline.Width.ToString("0"));
                sb.AppendLine("boneson=" + (ZombieOutline.BonesOn ? 1 : 0));
                sb.AppendLine("diron=" + (ZombieOutline.DirOn ? 1 : 0));
                sb.AppendLine("outfps=" + ZombieOutline.Hz.ToString("0"));
                sb.AppendLine("aimsmooth=" + AimLine.FollowSmooth.ToString("0.00"));
                sb.AppendLine("autododge=" + (Tracker.AutoDodgeOn ? 1 : 0));
                sb.AppendLine("autoqte=" + (AutoQTE.On ? 1 : 0));
                sb.AppendLine("lang=" + UI.Lang);
                sb.AppendLine("dodgedelay=" + Tracker.DelayMs.ToString("0"));
                System.IO.File.WriteAllText(PathCfg, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception e)
            {
                AutoDodgePlugin.L?.LogError("[自动闪避][状态] 保存失败: " + e.Message);
            }
        }
    }

    // 瞄中吸附：按住右键时把玩家的瞄准方向改成指向最近的僵尸（游戏本身的瞄准/手电筒都吃这个输入）
    [HarmonyPatch(typeof(DRS.Game.Character.Systems.AimingSystem.CharacterAimingSystem), "DRS_Game_Character_Systems_ICharacterSystem_Simulate")]
    public static class Patch_AimAssist
    {
        static float _next;
        static int _cnt;
        static bool _errOnce;

        static void Prefix(DRS.Game.Character.Systems.AimingSystem.CharacterAimingSystem __instance, float deltatime, ref DRS.Game.Input.CharacterInputs forInputs)
        {
            try
            {
                if (!Cheats.AimLine) return;
                bool rmb = false;
                try
                {
                    var m = UnityEngine.InputSystem.Mouse.current;
                    if (m != null) rmb = m.rightButton.isPressed;
                }
                catch { }
                if (!rmb) return;
                var sm = __instance.m_owner;
                if (sm == null) return;
                if (!Tracker.IsPlayer(sm)) return;
                Vector3 tp;
                if (!Tracker.TryNearestEnemyPos(out tp)) return;
                Vector3 me = Tracker.PlayerPos;
                float dx = tp.x - me.x, dy = tp.y - me.y;
                float L = (float)Math.Sqrt(dx * dx + dy * dy);
                if (L < 0.01f) return;
                Vector2 old = forInputs.AimDirection;
                forInputs.AimDirection = new Vector2(dx / L, dy / L);
                if (Time.realtimeSinceStartup > _next && _cnt < 12)
                {
                    _next = Time.realtimeSinceStartup + 1f;
                    _cnt++;
                    AutoDodgePlugin.L?.LogInfo("[瞄准线][吸附] old=(" + old.x.ToString("0.00") + "," + old.y.ToString("0.00")
                        + ") new=(" + (dx / L).ToString("0.00") + "," + (dy / L).ToString("0.00")
                        + ") me=(" + me.x.ToString("0.0") + "," + me.y.ToString("0.0")
                        + ") tgt=(" + tp.x.ToString("0.0") + "," + tp.y.ToString("0.0") + ")");
                }
            }
            catch (Exception e)
            {
                if (!_errOnce)
                {
                    _errOnce = true;
                    AutoDodgePlugin.L?.LogError("[瞄准线][吸附] 异常 " + e.Message);
                }
            }
        }
    }

    // 食物保鲜：跳过所有物品变质推进，食物永远是新鲜的
    [HarmonyPatch(typeof(DRS.Game.Inventory.ItemDegradationManager), "ProgressShelterDegradation")]
    public static class Patch_CheatFreshShelter
    {
        static bool Prefix()
        {
            try { return !Cheats.FreshFood; } catch { return true; }
        }
    }

    [HarmonyPatch(typeof(DRS.Game.Inventory.ItemDegradationManager), "ProgressLocationDegradation")]
    public static class Patch_CheatFreshLocation
    {
        static bool Prefix()
        {
            try { return !Cheats.FreshFood; } catch { return true; }
        }
    }

    // 无限生命：玩家永远被判为免疫伤害
    [HarmonyPatch(typeof(DRS.Game.Character.CharacterRoot), "DRS_Game_Damage_IDamageImmunityProvider_IsDamageImmune")]
    public static class Patch_CheatGod
    {
        static void Postfix(DRS.Game.Character.CharacterRoot __instance, ref bool __result)
        {
            try
            {
                if (Cheats.God && Tracker.IsPlayerRoot(__instance)) __result = true;
            }
            catch { }
        }
    }

    // 伤害倍率 / 一击必杀：放大玩家打出去落在敌人身上的伤害
    [HarmonyPatch(typeof(DRS.Game.Character.CharacterRoot), "DRS_Game_Damage_IDamageReceiver_Damage")]
    public static class Patch_CheatDamageScale
    {
        static void Prefix(DRS.Game.Character.CharacterRoot __instance, DRS.Game.Damage.Damage __0)
        {
            try
            {
                if (Cheats.DamageMode == 0 || __0 == null) return;
                if (Tracker.IsPlayerRoot(__instance)) return;
                float amt = __0.DamageAmount;
                if (amt <= 0f) return;
                __0.DamageAmount = Cheats.DamageMode == 2 ? 100000f : amt * 5f;
            }
            catch { }
        }
    }

    // 无限耐力：消耗耐力时把消耗量置 0
    [HarmonyPatch(typeof(DRS.Game.Survivor.Survivor), "DRS_Game_Character_Status_IStaminaStatusProvider_UseStamina")]
    public static class Patch_CheatStamina
    {
        static void Prefix(DRS.Game.Survivor.Survivor __instance, ref float __0)
        {
            try
            {
                if (Cheats.InfiniteStamina && __0 > 0f) __0 = 0f;
            }
            catch { }
        }
    }

    // 无饥饿：需求扣减（负值）时置 0
    [HarmonyPatch(typeof(DRS.Game.ShelterCharacter.Needs.CharacterPhysiologicalNeedsHandler), "AlterNeed")]
    public static class Patch_CheatHunger
    {
        static void Prefix(ref float __1)
        {
            try
            {
                if (Cheats.NoHunger && __1 < 0f) __1 = 0f;
            }
            catch { }
        }
    }

    // 无限弹药：每次开火后把弹匣写回满值
    [HarmonyPatch(typeof(DRS.Game.Character.Equipment.Weaponry.Components.AmmoComponent), "OnTrigger")]
    public static class Patch_CheatAmmo
    {
        static void Postfix(DRS.Game.Character.Equipment.Weaponry.Components.AmmoComponent __instance)
        {
            try
            {
                if (!Cheats.InfiniteAmmo) return;
                var model = __instance.m_ammoModel;
                if (model == null) return;
                var obs = model.CurrentAmmoLoaded;
                if (obs == null) return;
                if (obs.Value < 999) obs.Value = 999;
            }
            catch { }
        }
    }
}
