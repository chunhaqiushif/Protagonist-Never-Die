using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ProtagonistNeverDie
{
    /// <summary>
    /// 异星遗物插槽耳机 · C# 版（LuaCs 内存内模组）
    ///
    /// 与 Lua 版逻辑一一对应，差异只在实现方式：
    ///   * 每帧入口：协程（CoroutineManager）+ 可选的 Harmony 后缀，双通道冗余；
    ///     同一帧内第二次调用 dt 会是 0，不会重复结算
    ///   * 时间源：Timing.TotalTimeUnpaused（双端都有，且暂停时不增长），
    ///     不使用 GameTime / GameMain.Paused —— 那两个是客户端专属（MonoGame / 客户端分部），
    ///     专用服务器上不存在，会导致编译失败
    ///   * 延迟动作：内部调度表（按未暂停时间计时），替代 Lua 的 Timer.Wait
    ///
    /// 只跑在「单机 / 主机 / 专用服」；联机客户端不结算，病症由服务端同步。
    /// </summary>
    public sealed partial class ProtagonistNeverDiePlugin : IAssemblyPlugin
    {
        private const string HarmonyId = "protagonistneverdie.cs";
        private const string CoroutineName = "ProtagonistNeverDieTick";

        private Harmony _harmony;

        public void Initialize()
        {
            LuaCsLogger.Log("[Protagonist Never Die] 异星遗物插槽耳机 C# 版已加载（CSharp/Shared/ProtagonistNeverDie.cs）");

            // 通道 1：协程 —— 每帧一步，双端都有，不依赖 MonoGame
            try
            {
                CoroutineManager.StartCoroutine(TickLoop(), CoroutineName);
            }
            catch (Exception e)
            {
                LuaCsLogger.Log("[Protagonist Never Die] 协程启动失败：" + e.Message);
            }

            // 通道 2：GameMain.Update 存在时再挂一个 Harmony 后缀
            //   服务器版的 Update 可能没有参数（不依赖 MonoGame），所以这里按名字找、不写死签名
            //   同帧第二次调用时 dt = 0，不会重复结算
            try
            {
                _harmony = new Harmony(HarmonyId);

                MethodInfo update = AccessTools.Method(typeof(GameMain), "Update");
                if (update != null)
                {
                    _harmony.Patch(update, postfix: new HarmonyMethod(
                        typeof(ProtagonistNeverDiePlugin).GetMethod(nameof(TickPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                else
                {
                    LuaCsLogger.Log("[Protagonist Never Die] 未找到 GameMain.Update，仅用协程驱动");
                }

                // 拦截「把耐久已归零的芯片从耳机里取出」：
                //   UI 层已用 NonInteractable 挡住拖拽，这里兜住联机时客户端绕过界面直接发请求的情况。
                //   销毁流程会先把物品置入移除队列（IsInRemoveQueue = true），必须放行。
                MethodInfo removeItem = AccessTools.Method(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(Item) });
                if (removeItem != null)
                {
                    _harmony.Patch(removeItem, prefix: new HarmonyMethod(
                        typeof(Core).GetMethod(nameof(Core.RemoveItemPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                else
                {
                    LuaCsLogger.Log("[Protagonist Never Die] 未找到 Inventory.RemoveItem，芯片锁定只在 UI 层生效");
                }
            }
            catch (Exception e)
            {
                LuaCsLogger.Log("[Protagonist Never Die] Harmony 补丁跳过：" + e.Message);
            }
        }

        public void OnLoadCompleted() { }

        public void PreInitPatching() { }

        public void Dispose()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            try { CoroutineManager.StopCoroutines(CoroutineName); } catch { }
            Core.Reset();
            LuaCsLogger.Log("[Protagonist Never Die] C# 版已卸载");
        }

        /// <summary>Harmony 后缀（无参数，避免引用 GameTime）。</summary>
        private static void TickPostfix() => Core.Tick();

        /// <summary>协程驱动：每帧 yield 一次。</summary>
        private static IEnumerable<CoroutineStatus> TickLoop()
        {
            while (true)
            {
                Core.Tick();
                yield return CoroutineStatus.Running;
            }
        }
    }

    internal static class Core
    {
        // ---------------------------------------------------------------------
        // 常量：物品 / 病症标识符
        // ---------------------------------------------------------------------
        private static readonly Identifier Tag = "artifact_slot_headset";

        private static readonly Identifier ChipImmortal = "immortal_chip";
        private static readonly Identifier ChipRespawn = "respawn_chip";
        private static readonly Identifier ChipTeleport = "teleport_chip";
        private static readonly Identifier ChipBuff = "buff_chip";
        private static readonly Identifier ChipMaxHealth = "maxhealth_chip";

        private static readonly HashSet<Identifier> ChipIdentifiers = new HashSet<Identifier>
        {
            ChipImmortal, ChipRespawn, ChipTeleport, ChipBuff, ChipMaxHealth,
        };

        private static readonly Identifier RespawnPointId = "respawn_point";

        private static readonly Identifier AffMiracle = "custom_miracleworker";
        private static readonly Identifier AffPressure = "custom_pressurestabilized";
        private static readonly Identifier AffReaper = "custom_reaperstax";
        private static readonly Identifier AffIgnored = "custom_IgnoredByEnemyAI";
        private static readonly Identifier AffDrag = "custom_emergencyresponse";
        private static readonly Identifier AffHusk = "huskinfection";
        private static readonly Identifier AffSymbiosis = "husksymbiosis";
        private static readonly Identifier AffBlessing = "custom_respawnblessing";
        private static readonly Identifier AffMaxHealthBuff = "custom_maxheathbuff";
        private static readonly Identifier AffRespawnReady = "custom_respawnready";
        private static readonly Identifier AffRespawnCd = "custom_respawncooldown";
        private static readonly Identifier AffTeleportReady = "custom_teleportready";
        private static readonly Identifier AffTeleportCd = "custom_teleportcooldown";

        private static readonly Identifier[] ChipStatusAfflictions =
        {
            AffRespawnReady, AffRespawnCd, AffTeleportReady, AffTeleportCd,
        };

        /// <summary>复生祝福期间会被削掉的持续掉血类病症。</summary>
        private static readonly HashSet<Identifier> CleanseTypes = new HashSet<Identifier>
        {
            "damage", "bleeding", "burn", "oxygenlow", "poison", "bloodloss",
        };

        // ---------------------------------------------------------------------
        // 可调参数（对应 Lua 版的 H.CFG / H.Revive）
        // ---------------------------------------------------------------------
        private const float TickInterval = 0.1f;          // 结算间隔（秒）
        private const float MaxStep = 0.5f;               // 单次补偿上限，防卡顿后跳帧

        private const float PressureThreshold = 80f;      // 压力保护低于多少米触发水压奇迹
        private const float HuskInfectionLimit = 75f;     // 画皮感染强度达到该值后不再保护
        private const float PressureReducePerTick = 100f; // 已有抗压能力时每 tick 削减的水压奇迹强度
        private const float BotHealPerSecond = 2f;        // NPC 自愈速度（当前停用）

        private const float DeathThreshold = -95f;        // 没被任何芯片救回来时吃「主角的代价」
        private const float TeleportThreshold = -50f;     // 传送芯片触发阈值
        private const float RespawnThreshold = -65f;      // 复生芯片触发阈值

        private const float HealPerSecond = 15f;          // 复生祝福每秒治疗的病症强度
        private const float PenaltyReducePerSecond = 10f; // 复生祝福每秒削掉的「主角的代价」
        private const int TeleportDelayMs = 200;          // 传送前播特效的提前量
        private const int BlessingDelayMs = 2660;         // 除颤音效到回血生效的间隔
        private const float TeleportCooldownSeconds = 60f;   // 传送芯片冷却（秒）
        private const float RespawnCooldownSeconds = 120f;    // 复生芯片冷却（秒）

        private const float AffFullThreshold = 0.999f;    // 常驻 buff 低于该强度视为「未满」，下个 tick 补满
        private const int ChipDestroyDelayMs = 5000;      // 耐久归零 → 锁定，N 秒后销毁

        private const float TeleportChipLoss = 0.15f;     // 传送芯片耐久消耗（玩家）
        private const float TeleportChipLossBot = 0.05f;  // 传送芯片耐久消耗（NPC）
        private const float RespawnChipLoss = 0.25f;      // 复生芯片耐久消耗（玩家）
        private const float RespawnChipLossBot = 0.10f;   // 复生芯片耐久消耗（NPC）

        // ---------------------------------------------------------------------
        // 运行时状态
        // ---------------------------------------------------------------------
        private static readonly Dictionary<ushort, bool> BuffChipHolders = new Dictionary<ushort, bool>();
        private static readonly Dictionary<ushort, bool> ImmortalChipHolders = new Dictionary<ushort, bool>();
        private static readonly Dictionary<ushort, bool> MaxHealthChipHolders = new Dictionary<ushort, bool>();
        private static readonly Dictionary<ushort, bool> PendingBless = new Dictionary<ushort, bool>();
        private static readonly Dictionary<ushort, double> TeleportCooldown = new Dictionary<ushort, double>();
        private static readonly Dictionary<ushort, double> ReviveCooldown = new Dictionary<ushort, double>();
        private static readonly HashSet<ushort> DestroyScheduled = new HashSet<ushort>();
        private static readonly Dictionary<Identifier, AfflictionPrefab> PrefabCache = new Dictionary<Identifier, AfflictionPrefab>();

        private struct ScheduledAction
        {
            public double Due;
            public Action Action;
        }

        private static readonly List<ScheduledAction> Scheduled = new List<ScheduledAction>();
        private static readonly Random Rng = new Random();

        private static double _clock;         // 未暂停时钟（Timing.TotalTimeUnpaused，单位秒）
        private static double _lastClock = -1; // 上一帧的时间戳；< 0 表示还没跑过第一帧
        private static float _elapsed;        // 距离下次结算的累计
        private static int _errorCount;

        public static void Reset()
        {
            BuffChipHolders.Clear();
            ImmortalChipHolders.Clear();
            MaxHealthChipHolders.Clear();
            PendingBless.Clear();
            TeleportCooldown.Clear();
            ReviveCooldown.Clear();
            DestroyScheduled.Clear();
            PrefabCache.Clear();
            Scheduled.Clear();
            _clock = 0;
            _lastClock = -1;
            _elapsed = 0f;
            _errorCount = 0;
        }

        // ---------------------------------------------------------------------
        // 每帧入口
        // ---------------------------------------------------------------------
        /// <summary>
        /// 每帧调用。时间源用 Timing.TotalTimeUnpaused：
        ///   * 双端（客户端 / 专用服务器）都有，不依赖 MonoGame
        ///   * 本身就是「暂停不增长」，所以不需要再判断 GameMain.Paused（那个属性只在客户端分部里）
        /// </summary>
        public static void Tick()
        {
            try
            {
                if (!ShouldRun()) return;

                double now = Timing.TotalTimeUnpaused;

                // 首帧 / 时间回退（读档、重载）时 dt 记 0，避免一次结算一大段
                double delta = _lastClock < 0 ? 0 : now - _lastClock;
                _lastClock = now;
                if (delta < 0) delta = 0;
                float deltaTime = (float)delta;

                _clock = now; // 冷却 / 延迟动作统一用这个未暂停时钟

                RunScheduled();

                _elapsed += deltaTime;
                if (_elapsed < TickInterval) return;

                float step = _elapsed;
                _elapsed = 0f;
                if (step > MaxStep) step = MaxStep;

                List<Character> characters = Character.CharacterList;
                if (characters == null) return;

                // 倒序遍历：允许中途移除
                for (int i = characters.Count - 1; i >= 0; i--)
                {
                    try { UpdateCharacter(characters[i], step); }
                    catch (Exception e) { LogError(e); }
                }
            }
            catch (Exception e)
            {
                LogError(e);
            }
        }

        /// <summary>只在单机 / 主机 / 专用服结算；联机客户端交给服务端同步。</summary>
        private static bool ShouldRun()
        {
            NetworkMember net = GameMain.NetworkMember;
            if (net == null) return true;   // 单机
            return net.IsServer;            // 主机 / 专用服务器
        }

        private static void RunScheduled()
        {
            for (int i = Scheduled.Count - 1; i >= 0; i--)
            {
                if (Scheduled[i].Due > _clock) continue;
                Action action = Scheduled[i].Action;
                Scheduled.RemoveAt(i);
                try { action(); }
                catch (Exception e) { LogError(e); }
            }
        }

        private static void Schedule(int delayMs, Action action)
        {
            Scheduled.Add(new ScheduledAction { Due = _clock + delayMs / 1000.0, Action = action });
        }

        private static void LogError(Exception e)
        {
            if (_errorCount >= 5) return;
            _errorCount++;
            LuaCsLogger.Log("[Protagonist Never Die] 逻辑出错：" + e);
        }

        // ---------------------------------------------------------------------
        // 单角色结算
        // ---------------------------------------------------------------------
        private static void UpdateCharacter(Character character, float dt)
        {
            if (character == null || character.Removed) return;

            // 尸体：只保留「传送芯片把尸体送回船上」
            if (character.IsDead)
            {
                TryTeleportCorpse(character);
                return;
            }

            CharacterInventory inventory = character.Inventory;
            if (inventory == null) return;

            Item headset = inventory.FindEquippedItemByTag(Tag);
            if (headset == null)
            {
                if (BuffChipHolders.ContainsKey(character.ID)) RemovePermanentBuffs(character);
                if (ImmortalChipHolders.ContainsKey(character.ID)) RemoveImmortalBuffs(character);
                if (MaxHealthChipHolders.Remove(character.ID)) RemoveAfflictionByIdentifier(character.CharacterHealth, AffMaxHealthBuff);
                ClearChipStatus(character);
                // 已经生效的复生祝福不因摘耳机而中断
                TickBlessingHeal(character, dt);
                return;
            }

            CharacterHealth health = character.CharacterHealth;
            if (health == null) return;

            Dictionary<Identifier, Item> chips = CollectChips(headset);
            chips.TryGetValue(ChipRespawn, out Item respawnChip);
            chips.TryGetValue(ChipTeleport, out Item teleportChip);
            chips.TryGetValue(ChipBuff, out Item buffChip);
            chips.TryGetValue(ChipImmortal, out Item immortalChip);
            chips.TryGetValue(ChipMaxHealth, out Item maxHealthChip);

            // 1) 增益芯片：全速拖拽
            //   幂等：每 tick 检查强度，不足就补满。原先只在「首次插入」时施加一次，
            //   之后强度若因 prefab 缓存失效 / 外部清除而掉下来，就再也补不回去
            if (buffChip != null)
            {
                ApplyPermanentBuffs(character);
            }
            else if (BuffChipHolders.ContainsKey(character.ID))
            {
                RemovePermanentBuffs(character);
            }

            // 1.5) 免死芯片：奇迹进行中 + 水压奇迹
            if (immortalChip != null)
            {
                ApplyImmortalBuffs(character);   // 幂等：强度不足会补满

                bool immuneToPressure = character.IsImmuneToPressure;
                if (!immuneToPressure
                    && character.InPressure
                    && character.PressureProtection < PressureThreshold
                    && GetStrength(character, AffSymbiosis) < 1f
                    && GetStrength(character, AffHusk) < HuskInfectionLimit
                    && !character.IsProtectedFromPressure)
                {
                    ApplyFull(character, AffPressure);
                }

                if (immuneToPressure) ReduceStrength(character, AffPressure, PressureReducePerTick);
            }
            else if (ImmortalChipHolders.ContainsKey(character.ID))
            {
                RemoveImmortalBuffs(character);
            }

            // 1.6) 生命芯片：最大生命 +25%
            //   custom_maxheathbuff 是 strengthchange=0（不衰减），但同样需要幂等补满：
            //   只在强度真的到顶时跳过，否则每 tick 补齐
            if (maxHealthChip != null)
            {
                MaxHealthChipHolders[character.ID] = true;
                if (GetStrength(character, AffMaxHealthBuff) < AffFullThreshold)
                {
                    ApplyFull(character, AffMaxHealthBuff);
                }
            }
            else if (MaxHealthChipHolders.Remove(character.ID))
            {
                RemoveAfflictionByIdentifier(health, AffMaxHealthBuff);
            }

            // 2) NPC 自愈（原版画皮共生的再生会抵消内脏损伤，暂时停用）
            // if (character.IsBot && health.HealthPercentage > 80f && health.HealthPercentage < 100f)
            //     HealDamage(character, BotHealPerSecond * dt);

            // 3) 濒死处理
            bool reviving = HasAffliction(character, AffBlessing);

            // 拔掉复生芯片：只取消除颤等待阶段，已生效的祝福让它自然走完
            if (respawnChip == null) PendingBless.Remove(character.ID);

            if (!reviving && !PendingBless.ContainsKey(character.ID))
            {
                float vitality = VitalityPercent(character);
                bool rescued = false;

                // 3.1) 传送芯片
                if (teleportChip != null && vitality <= TeleportThreshold)
                {
                    bool teleported = TryTeleport(character, headset, teleportChip);
                    rescued |= teleported;

                    // 传送真的生效时，顺手再触发一次复生（两颗芯片各扣一次耐久）
                    if (teleported && respawnChip != null) rescued |= TryBless(character, headset, respawnChip);
                }

                // 3.2) 复生芯片
                if (respawnChip != null && vitality <= RespawnThreshold)
                {
                    rescued |= TryBless(character, headset, respawnChip);
                }

                // 3.3) 都没救回来且已到 -95%：吃代价
                if (!rescued && vitality <= DeathThreshold)
                {
                    ApplyFull(character, AffReaper);
                    ApplyFull(character, AffIgnored);
                }
            }

            // 3.5) 复生祝福持续治疗
            reviving = TickBlessingHeal(character, dt);

            // 4) 芯片状态图标
            UpdateChipStatus(character, chips, reviving, health);

            // 5) 耐久归零的芯片：锁定 + 结算完成后的销毁 / 兜底清理
            MaintainDepletedChips(character, chips);
        }

        // ---------------------------------------------------------------------
        // 复生祝福治疗
        // ---------------------------------------------------------------------
        private static bool TickBlessingHeal(Character character, float dt)
        {
            if (character == null || character.Removed || character.IsDead) return false;

            CharacterHealth health = character.CharacterHealth;
            if (health == null) return false;

            Affliction blessing = health.GetAffliction(AffBlessing, true);
            if (blessing == null || blessing.Strength <= 0f) return false;

            ReduceStrength(character, AffReaper, PenaltyReducePerSecond * dt);
            CleanseAfflictions(character, HealPerSecond * dt);
            return true;
        }

        // ---------------------------------------------------------------------
        // 传送
        // ---------------------------------------------------------------------
        private static bool TryTeleport(Character character, Item headset, Item chip)
        {
            if (!ChipUsable(chip)) return false;
            if (IsOnMainSub(character)) return false;
            if (InCooldown(TeleportCooldown, chip, TeleportCooldownSeconds)) return false;

            Item point = FindNearestRespawnPoint(character);
            Vector2? destination = point != null ? point.WorldPosition : FindRandomSpawnPoint(character);
            if (destination == null) return false;

            TeleportCooldown[chip.ID] = _clock;

            DamageChip(chip, character, TeleportChipLoss, TeleportChipLossBot);

            PlayTeleportFx(character, headset);
            Vector2 target = destination.Value;
            Schedule(TeleportDelayMs, () =>
            {
                try { if (character != null && !character.Removed) character.TeleportTo(target); }
                catch (Exception e) { LogError(e); }
                PlayTeleportFx(character, headset);
                // 芯片销毁统一由「耐久归零 → 3 秒计时」处理，这里不再介入
            });

            LuaCsLogger.Log("[Protagonist Never Die] " + character.Name + " 触发传送芯片");
            return true;
        }

        private static void TryTeleportCorpse(Character character)
        {
            if (IsOnMainSub(character)) return;

            CharacterInventory inventory = character.Inventory;
            if (inventory == null) return;

            Item headset = inventory.FindEquippedItemByTag(Tag);
            if (headset == null) return;

            if (!CollectChips(headset).TryGetValue(ChipTeleport, out Item chip)) return;

            TryTeleport(character, headset, chip);
        }

        private static void PlayTeleportFx(Character character, Item headset)
        {
            if (headset == null || character == null) return;
            try
            {
                headset.ApplyStatusEffects(ActionType.OnUse, 0f, character, null, null, true, character.WorldPosition);
            }
            catch (Exception e) { LogError(e); }
        }

        private static Item FindNearestRespawnPoint(Character character)
        {
            List<Item> list = Item.ItemList;
            if (list == null) return null;

            Vector2 position = character.WorldPosition;
            Item best = null;
            float bestDistance = float.MaxValue;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                Item item = list[i];
                if (item == null || item.Removed) continue;
                if (item.Prefab == null || item.Prefab.Identifier != RespawnPointId) continue;

                float distance = Vector2.DistanceSquared(position, item.WorldPosition);
                if (distance < bestDistance)
                {
                    best = item;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private static Vector2? FindRandomSpawnPoint(Character character)
        {
            List<WayPoint> list = WayPoint.WayPointList;
            if (list == null) return null;

            Submarine submarine = Submarine.MainSub ?? character.Submarine;
            bool filterSub = submarine != null;
            ushort subId = filterSub ? submarine.ID : (ushort)0;

            List<Vector2> candidates = new List<Vector2>();
            for (int i = 0; i < list.Count; i++)
            {
                WayPoint waypoint = list[i];
                if (waypoint == null) continue;
                if (waypoint.SpawnType != SpawnType.Human) continue;

                if (filterSub)
                {
                    Submarine wpSub = waypoint.Submarine;
                    if (wpSub != null && wpSub.ID != subId) continue;
                }

                candidates.Add(waypoint.WorldPosition);
            }

            if (candidates.Count == 0) return null;
            return candidates[Rng.Next(candidates.Count)];
        }

        /// <summary>是否待在自己的主潜艇里（只认 Submarine.MainSub）。</summary>
        private static bool IsOnMainSub(Character character)
        {
            Submarine sub = character.Submarine;
            Submarine main = Submarine.MainSub;
            if (sub == null || main == null) return false;
            return sub.ID == main.ID;
        }

        // ---------------------------------------------------------------------
        // 复生
        // ---------------------------------------------------------------------
        private static bool TryBless(Character character, Item headset, Item chip)
        {
            if (!ChipUsable(chip)) return false;

            CharacterHealth health = character.CharacterHealth;
            if (health == null) return false;

            if (HasAffliction(character, AffBlessing)) return false;
            if (PendingBless.ContainsKey(character.ID)) return false;
            if (InCooldown(ReviveCooldown, chip, RespawnCooldownSeconds)) return false;

            ReviveCooldown[chip.ID] = _clock;
            PendingBless[character.ID] = true;

            DamageChip(chip, character, RespawnChipLoss, RespawnChipLossBot);

            PlayDefibSound(character, headset);
            Schedule(BlessingDelayMs, () =>
            {
                // 期间被拔芯片 / 取消：放弃
                if (!PendingBless.Remove(character.ID)) return;
                if (character == null || character.Removed || character.IsDead) return;
                ApplyFull(character, AffBlessing);
                // 芯片销毁统一由「耐久归零 → 3 秒计时」处理，这里不再介入
            });

            LuaCsLogger.Log("[Protagonist Never Die] " + character.Name + " 触发复生芯片（除颤中）");
            return true;
        }

        private static void PlayDefibSound(Character character, Item headset)
        {
            if (headset == null || character == null) return;
            try
            {
                headset.ApplyStatusEffects(ActionType.OnSecondaryUse, 0f, character, null, null, true, character.WorldPosition);
            }
            catch (Exception e) { LogError(e); }
        }

        // ---------------------------------------------------------------------
        // 冷却
        // ---------------------------------------------------------------------
        private static bool InCooldown(Dictionary<ushort, double> records, Item chip, float seconds)
        {
            if (chip == null) return false;
            return records.TryGetValue(chip.ID, out double last) && _clock - last < seconds;
        }

        private static float CooldownRatio(Dictionary<ushort, double> records, Item chip, float seconds)
        {
            if (chip == null || seconds <= 0f) return 0f;
            if (!records.TryGetValue(chip.ID, out double last)) return 0f;

            double remaining = seconds - (_clock - last);
            if (remaining <= 0.0)
            {
                records.Remove(chip.ID);
                return 0f;
            }
            return (float)(remaining / seconds);
        }

        // ---------------------------------------------------------------------
        // 芯片耐久
        // ---------------------------------------------------------------------
        private static bool ChipUsable(Item chip)
        {
            if (chip == null || chip.Removed) return false;
            return chip.Condition > 0f;
        }

        private static void DamageChip(Item chip, Character character, float playerLoss, float botLoss)
        {
            if (chip == null) return;

            float maxCondition = chip.MaxCondition;
            if (maxCondition <= 0f) maxCondition = 100f;

            float loss = character != null && character.IsBot ? botLoss : playerLoss;
            float newCondition = chip.Condition - maxCondition * loss;
            if (newCondition < 0f) newCondition = 0f;

            chip.Condition = newCondition;

            if (chip.Condition <= 0f)
            {
                // 归零瞬间由引擎自动执行 XML 里的 OnBroken（音效 / 爆炸 / 失血）
                chip.NonInteractable = true;          // 立即锁定，玩家拿不走
                ScheduleChipDestroy(chip);            // 3 秒后移除物品
            }
        }

        /// <summary>排入「N 秒后删除」的计时，同一颗芯片只排一次。</summary>
        private static void ScheduleChipDestroy(Item chip)
        {
            if (chip == null || chip.Removed) return;
            if (!DestroyScheduled.Add(chip.ID)) return;

            Schedule(ChipDestroyDelayMs, () =>
            {
                DestroyScheduled.Remove(chip.ID);

                // 计时期间被修理 / 耐久被改回正数：取消销毁
                if (chip.Condition > 0f) return;

                DestroyChip(chip);
            });
        }

        /// <summary>把芯片排进移除队列（实际删除发生在下一帧）。</summary>
        private static void DestroyChip(Item chip)
        {
            if (chip == null || chip.Removed || chip.IsInRemoveQueue) return;
            try { Entity.Spawner.AddItemToRemoveQueue(chip); }
            catch (Exception e) { LogError(e); }
        }

        /// <summary>
        /// 维护「耐久归零」芯片的状态：
        ///   * 归零 → 锁住（UI 层拿不走），并确保已排入销毁计时
        ///   * 被修理 / 外部改回正耐久 → 解锁
        ///   * 读档等场景下耐久已是 0 但没有计时器 → 这里补排
        /// </summary>
        private static void MaintainDepletedChips(Character character, Dictionary<Identifier, Item> chips)
        {
            foreach (Item chip in chips.Values)
            {
                if (chip == null) continue;

                chip.NonInteractable = chip.Condition <= 0f;
                if (chip.Condition > 0f) continue;

                ScheduleChipDestroy(chip);
            }
        }

        /// <summary>
        /// Harmony Prefix：阻止把耐久已归零的芯片从耳机里取出。
        /// 只在「宿主是耳机容器」+「耐久归零」+「不在移除队列」时拦截，其余一律放行。
        /// </summary>
        internal static bool RemoveItemPrefix(Inventory __instance, Item item)
        {
            try
            {
                if (item == null || item.Removed || item.Condition > 0f || item.IsInRemoveQueue) return true;

                ItemContainer container = (__instance as ItemInventory)?.Container;
                Item owner = container?.Item;
                if (owner == null || !owner.HasTag(Tag)) return true;

                // 耳机本体正在被销毁 / 拆解时放行，否则 0 耐久芯片会残留在被清空的容器里
                if (owner.Removed || owner.IsInRemoveQueue) return true;

                return false;   // 拒绝取出
            }
            catch
            {
                return true;
            }
        }

        // ---------------------------------------------------------------------
        // 芯片识别
        // ---------------------------------------------------------------------
        private static Dictionary<Identifier, Item> CollectChips(Item ownerItem)
        {
            Dictionary<Identifier, Item> result = new Dictionary<Identifier, Item>();
            if (ownerItem == null) return result;

            ItemContainer container = ownerItem.GetComponent<ItemContainer>();
            if (container == null) return result;

            foreach (Item contained in container.Inventory.AllItems)
            {
                if (contained == null || contained.Prefab == null) continue;
                Identifier identifier = contained.Prefab.Identifier;
                if (!ChipIdentifiers.Contains(identifier)) continue;
                if (!result.ContainsKey(identifier)) result[identifier] = contained;
            }

            return result;
        }

        // ---------------------------------------------------------------------
        // 病症工具
        // ---------------------------------------------------------------------
        private static AfflictionPrefab GetPrefab(Identifier identifier)
        {
            // 每次都从注册表取当前实例，再与缓存比对：
            //   * 早期调用可能取不到（加载顺序 / 重载），不能把 null 写进缓存，否则之后永远拿不到
            //   * 游戏内重载 mod 后注册表会重建，旧 prefab 实例 Instantiate 出的 affliction 与新注册表不匹配
            AfflictionPrefab registered = null;
            try { registered = AfflictionPrefab.Prefabs[identifier]; }
            catch (Exception e) { LogError(e); }

            if (registered == null) return null;

            if (PrefabCache.TryGetValue(identifier, out AfflictionPrefab cached)
                && ReferenceEquals(cached, registered))
            {
                return cached;
            }

            PrefabCache[identifier] = registered;
            return registered;
        }

        private static float GetStrength(Character character, Identifier identifier)
        {
            CharacterHealth health = character?.CharacterHealth;
            if (health == null) return 0f;
            return health.GetAfflictionStrengthByIdentifier(identifier, true);
        }

        private static bool HasAffliction(Character character, Identifier identifier)
        {
            CharacterHealth health = character?.CharacterHealth;
            if (health == null) return false;
            Affliction affliction = health.GetAffliction(identifier, true);
            return affliction != null && affliction.Strength > 0f;
        }

        /// <summary>把病症拉满（强度 = prefab.MaxStrength）。</summary>
        private static void ApplyFull(Character character, Identifier identifier)
        {
            AfflictionPrefab prefab = GetPrefab(identifier);
            if (prefab == null) return;

            CharacterHealth health = character?.CharacterHealth;
            if (health == null) return;

            Affliction current = health.GetAffliction(identifier, true);
            if (current == null)
            {
                health.ApplyAffliction(null, prefab.Instantiate(prefab.MaxStrength, character));
            }
            else if (current.Strength < prefab.MaxStrength)
            {
                current.SetStrength(prefab.MaxStrength);
            }
        }

        private static void ReduceStrength(Character character, Identifier identifier, float amount)
        {
            CharacterHealth health = character?.CharacterHealth;
            if (health == null) return;

            Affliction current = health.GetAffliction(identifier, true);
            if (current == null) return;

            current.SetStrength(Math.Max(0f, current.Strength - amount));
        }

        private static void RemoveAfflictionByIdentifier(CharacterHealth health, Identifier identifier)
        {
            if (health == null) return;

            try { health.RemoveAfflictions(a => a != null && a.Identifier == identifier); }
            catch (Exception e) { LogError(e); }

            Affliction current = health.GetAffliction(identifier, true);
            if (current != null && current.Strength > 0f) current.SetStrength(0f);
        }

        private static void ClearAffliction(CharacterHealth health, Identifier identifier)
        {
            if (health == null) return;
            if (health.GetAffliction(identifier, true) == null) return;
            RemoveAfflictionByIdentifier(health, identifier);
        }

        private static void SetAfflictionStrength(Character character, Identifier identifier, float value)
        {
            CharacterHealth health = character?.CharacterHealth;
            if (health == null) return;

            if (value <= 0f)
            {
                ClearAffliction(health, identifier);
                return;
            }

            AfflictionPrefab prefab = GetPrefab(identifier);
            if (prefab == null) return;

            Affliction current = health.GetAffliction(identifier, true);
            if (current == null) health.ApplyAffliction(null, prefab.Instantiate(value, character));
            else current.SetStrength(value);
        }

        /// <summary>按类型削掉持续掉血的病症（复生祝福）。</summary>
        private static void CleanseAfflictions(Character character, float amount)
        {
            CharacterHealth health = character?.CharacterHealth;
            if (health == null) return;

            foreach (Affliction affliction in health.GetAllAfflictions())
            {
                if (affliction == null || affliction.Strength <= 0f) continue;

                AfflictionPrefab prefab = affliction.Prefab;
                if (prefab == null) continue;

                if (CleanseTypes.Contains(prefab.AfflictionType))
                {
                    affliction.SetStrength(Math.Max(0f, affliction.Strength - amount));
                }
            }
        }

        /// <summary>削减所有 damage 类病症（NPC 自愈用，当前停用）。</summary>
        private static void HealDamage(Character character, float amount)
        {
            CharacterHealth health = character?.CharacterHealth;
            if (health == null) return;

            Identifier damageType = "damage";
            foreach (Affliction affliction in health.GetAllAfflictions())
            {
                if (affliction?.Prefab == null) continue;
                if (affliction.Prefab.AfflictionType == damageType)
                {
                    affliction.SetStrength(Math.Max(0f, affliction.Strength - amount));
                }
            }
        }

        private static float VitalityPercent(Character character)
        {
            float maxVitality = character.MaxVitality;
            if (maxVitality <= 0f) return 100f;
            return character.Vitality / maxVitality * 100f;
        }

        // ---------------------------------------------------------------------
        // 芯片提供的常驻增益
        // ---------------------------------------------------------------------
        private static void ApplyPermanentBuffs(Character character)
        {
            if (character == null) return;
            BuffChipHolders[character.ID] = true;
            if (GetStrength(character, AffDrag) < AffFullThreshold) ApplyFull(character, AffDrag);
        }

        private static void RemovePermanentBuffs(Character character)
        {
            if (character == null) return;
            BuffChipHolders.Remove(character.ID);
            RemoveAfflictionByIdentifier(character.CharacterHealth, AffDrag);
        }

        private static void ApplyImmortalBuffs(Character character)
        {
            if (character == null) return;
            ImmortalChipHolders[character.ID] = true;

            // 奇迹进行中只给友方；强度不足时补满
            if (character.TeamID == CharacterTeamType.Team1
                && GetStrength(character, AffMiracle) < AffFullThreshold)
            {
                ApplyFull(character, AffMiracle);
            }
        }

        private static void RemoveImmortalBuffs(Character character)
        {
            if (character == null) return;
            ImmortalChipHolders.Remove(character.ID);

            CharacterHealth health = character.CharacterHealth;
            RemoveAfflictionByIdentifier(health, AffMiracle);
            RemoveAfflictionByIdentifier(health, AffPressure);
        }

        // ---------------------------------------------------------------------
        // 芯片状态图标
        // ---------------------------------------------------------------------
        private static void UpdateChipStatus(Character character, Dictionary<Identifier, Item> chips, bool reviving, CharacterHealth health)
        {
            if (health == null) return;

            chips.TryGetValue(ChipRespawn, out Item respawnChip);
            if (respawnChip != null)
            {
                float ratio = CooldownRatio(ReviveCooldown, respawnChip, RespawnCooldownSeconds);

                if (ratio > 0f) SetAfflictionStrength(character, AffRespawnCd, ratio * 100f);
                else ClearAffliction(health, AffRespawnCd);

                if (ratio <= 0f && !reviving) ApplyFull(character, AffRespawnReady);
                else ClearAffliction(health, AffRespawnReady);
            }
            else
            {
                ClearAffliction(health, AffRespawnReady);
                ClearAffliction(health, AffRespawnCd);
            }

            chips.TryGetValue(ChipTeleport, out Item teleportChip);
            if (teleportChip != null)
            {
                float ratio = CooldownRatio(TeleportCooldown, teleportChip, TeleportCooldownSeconds);
                if (ratio > 0f)
                {
                    ClearAffliction(health, AffTeleportReady);
                    SetAfflictionStrength(character, AffTeleportCd, ratio * 100f);
                }
                else
                {
                    ClearAffliction(health, AffTeleportCd);
                    ApplyFull(character, AffTeleportReady);
                }
            }
            else
            {
                ClearAffliction(health, AffTeleportReady);
                ClearAffliction(health, AffTeleportCd);
            }
        }

        private static void ClearChipStatus(Character character)
        {
            CharacterHealth health = character?.CharacterHealth;
            if (health == null) return;

            for (int i = 0; i < ChipStatusAfflictions.Length; i++)
            {
                ClearAffliction(health, ChipStatusAfflictions[i]);
            }
        }
    }
}
