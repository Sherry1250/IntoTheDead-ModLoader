using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SamplePlugin
{
    /// <summary>
    /// 最小可用插件示例：证明框架通了，并演示两个关键写法
    /// 1) IL2CPP 的 Harmony 补丁必须用位置参数 __0 / __1，不能用参数名
    /// 2) 每帧驱动最稳的钩子是 Time.get_deltaTime
    /// </summary>
    [BepInPlugin("com.example.itd.sample", "ITD Sample Plugin", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public static ManualLogSource L;
        static int _frames;

        public override void Load()
        {
            L = Log;
            L.LogInfo("[Sample] 插件已加载，框架可用");

            // 按类分别挂载，避免一个补丁失败拖垮全部
            PatchOne(typeof(Patch_Heartbeat));
        }

        static void PatchOne(System.Type patchType)
        {
            var harmony = new Harmony("com.example.itd.sample." + patchType.Name);
            try
            {
                harmony.PatchAll(patchType);
                L.LogInfo("[Sample] 已挂补丁: " + patchType.Name);
            }
            catch (System.Exception e)
            {
                L.LogError("[Sample] " + patchType.Name + " 失败: " + e);
            }
        }

        public static void OnFrame()
        {
            _frames++;
            if (_frames % 2000 == 0)
                L.LogInfo("[Sample] 心跳 " + _frames + " 帧");
        }
    }

    /// <summary>
    /// 引擎层心跳：Time.deltaTime 每帧必被游戏读取，是最稳的驱动点。
    /// 注意参数名是 __0，不是 deltaTime。
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.Time), "get_deltaTime")]
    public static class Patch_Heartbeat
    {
        static void Postfix()
        {
            Plugin.OnFrame();
        }
    }

    /*
     * 挂钩游戏业务方法的示例（换成真实类型和方法名即可）：

     * [HarmonyPatch(typeof(DRS.Game.Character.CharacterRoot), "Simulate")]
     * public static class Patch_CharacterSimulate
     * {
     *     static void Prefix(DRS.Game.Character.CharacterRoot __instance, float __0)
     *     {
     *         // __0 是 deltaTime；这里能拿到角色 root
     *     }
     * }
     *
     * 接口实现的方法名是拼接形式，例如：
     *   typeof(CharacterRoot) 上的 "DRS_Game_Damage_IDamageReceiver_Damage"
     *   typeof(CharacterRoot) 上的 "DRS_Game_Damage_IDamageImmunityProvider_IsDamageImmune"
     */
}
