using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FeedLikeGrandma;

// Valheim 1.0.7 private bindings. Delegates are resolved once, never searched per frame.
// Public getters are used at call sites wherever they expose the same state.
internal static class GameAccess
{
    private static readonly AccessTools.FieldRef<BaseAI, Tameable> BaseAITameable = AccessTools.FieldRefAccess<BaseAI, Tameable>("m_tamable");
    internal static ref Tameable TameableComponent(this BaseAI instance) => ref BaseAITameable(instance);

    private static readonly AccessTools.FieldRef<Character, ZNetView> CharacterNetworkView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
    internal static ref ZNetView NetworkView(this Character instance) => ref CharacterNetworkView(instance);

    private static readonly AccessTools.FieldRef<Character, float> CharacterWaterLevel = AccessTools.FieldRefAccess<Character, float>("m_waterLevel");
    internal static ref float WaterLevel(this Character instance) => ref CharacterWaterLevel(instance);

    private static readonly AccessTools.FieldRef<Character, float> CharacterTarLevel = AccessTools.FieldRefAccess<Character, float>("m_tarLevel");
    internal static ref float TarLevel(this Character instance) => ref CharacterTarLevel(instance);

    private static readonly AccessTools.FieldRef<Container, ZNetView> ContainerNetworkView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
    internal static ref ZNetView NetworkView(this Container instance) => ref ContainerNetworkView(instance);

    private static readonly AccessTools.FieldRef<Container, Piece> ContainerContainerPiece = AccessTools.FieldRefAccess<Container, Piece>("m_piece");
    internal static ref Piece ContainerPiece(this Container instance) => ref ContainerContainerPiece(instance);

    private static readonly AccessTools.FieldRef<EggGrow, ZNetView> EggGrowNetworkView = AccessTools.FieldRefAccess<EggGrow, ZNetView>("m_nview");
    internal static ref ZNetView NetworkView(this EggGrow instance) => ref EggGrowNetworkView(instance);

    private static readonly AccessTools.FieldRef<EggGrow, ItemDrop> EggGrowEggItem = AccessTools.FieldRefAccess<EggGrow, ItemDrop>("m_item");
    internal static ref ItemDrop EggItem(this EggGrow instance) => ref EggGrowEggItem(instance);

    private static readonly AccessTools.FieldRef<Growup, ZNetView> GrowupNetworkView = AccessTools.FieldRefAccess<Growup, ZNetView>("m_nview");
    internal static ref ZNetView NetworkView(this Growup instance) => ref GrowupNetworkView(instance);

    private static readonly AccessTools.FieldRef<Procreation, ZNetView> ProcreationNetworkView = AccessTools.FieldRefAccess<Procreation, ZNetView>("m_nview");
    internal static ref ZNetView NetworkView(this Procreation instance) => ref ProcreationNetworkView(instance);

    private static readonly AccessTools.FieldRef<Procreation, GameObject> ProcreationOwnPrefab = AccessTools.FieldRefAccess<Procreation, GameObject>("m_myPrefab");
    internal static ref GameObject OwnPrefab(this Procreation instance) => ref ProcreationOwnPrefab(instance);

    private static readonly AccessTools.FieldRef<Procreation, GameObject> ProcreationOffspringPrefab = AccessTools.FieldRefAccess<Procreation, GameObject>("m_offspringPrefab");
    internal static ref GameObject OffspringPrefab(this Procreation instance) => ref ProcreationOffspringPrefab(instance);

    private static readonly AccessTools.FieldRef<Procreation, Tameable> ProcreationTameableComponent = AccessTools.FieldRefAccess<Procreation, Tameable>("m_tameable");
    internal static ref Tameable TameableComponent(this Procreation instance) => ref ProcreationTameableComponent(instance);

    private static readonly AccessTools.FieldRef<Sadle, ZNetView> SadleNetworkView = AccessTools.FieldRefAccess<Sadle, ZNetView>("m_nview");
    internal static ref ZNetView NetworkView(this Sadle instance) => ref SadleNetworkView(instance);

    private static readonly AccessTools.FieldRef<Sadle, int> SadleRidingSpeed = AccessTools.FieldRefAccess<Sadle, int>("m_speed");
    internal static ref int RidingSpeed(this Sadle instance) => ref SadleRidingSpeed(instance);

    private static readonly AccessTools.FieldRef<BaseAI, ZSyncAnimation> BaseAISyncedAnimator = AccessTools.FieldRefAccess<BaseAI, ZSyncAnimation>("m_animator");
    internal static ref ZSyncAnimation SyncedAnimator(this BaseAI instance) => ref BaseAISyncedAnimator(instance);

    private static readonly AccessTools.FieldRef<ZSyncAnimation, Animator> ZSyncAnimationUnityAnimator = AccessTools.FieldRefAccess<ZSyncAnimation, Animator>("m_animator");
    internal static ref Animator UnityAnimator(this ZSyncAnimation instance) => ref ZSyncAnimationUnityAnimator(instance);

    private static readonly AccessTools.FieldRef<Humanoid, float> HumanoidAttackDrawTime = AccessTools.FieldRefAccess<Humanoid, float>("m_attackDrawTime");
    internal static ref float AttackDrawTime(this Humanoid instance) => ref HumanoidAttackDrawTime(instance);

    private static readonly AccessTools.FieldRef<Inventory, int> InventoryInventoryWidth = AccessTools.FieldRefAccess<Inventory, int>("m_width");
    internal static ref int InventoryWidth(this Inventory instance) => ref InventoryInventoryWidth(instance);

    private static readonly AccessTools.FieldRef<Inventory, int> InventoryInventoryHeight = AccessTools.FieldRefAccess<Inventory, int>("m_height");
    internal static ref int InventoryHeight(this Inventory instance) => ref InventoryInventoryHeight(instance);

    private static readonly AccessTools.FieldRef<ObjectDB, Dictionary<int,GameObject>> ObjectDBItemHashes = AccessTools.FieldRefAccess<ObjectDB, Dictionary<int,GameObject>>("m_itemByHash");
    internal static ref Dictionary<int,GameObject> ItemHashes(this ObjectDB instance) => ref ObjectDBItemHashes(instance);

    private static readonly AccessTools.FieldRef<ObjectDB, Dictionary<ItemDrop.ItemData.SharedData,GameObject>> ObjectDBItemDataPrefabs = AccessTools.FieldRefAccess<ObjectDB, Dictionary<ItemDrop.ItemData.SharedData,GameObject>>("m_itemByData");
    internal static ref Dictionary<ItemDrop.ItemData.SharedData,GameObject> ItemDataPrefabs(this ObjectDB instance) => ref ObjectDBItemDataPrefabs(instance);

    private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<int,GameObject>> ZNetSceneNamedPrefabs = AccessTools.FieldRefAccess<ZNetScene, Dictionary<int,GameObject>>("m_namedPrefabs");
    internal static ref Dictionary<int,GameObject> NamedPrefabs(this ZNetScene instance) => ref ZNetSceneNamedPrefabs(instance);

    private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO,ZNetView>> ZNetSceneInstances = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO,ZNetView>>("m_instances");
    internal static ref Dictionary<ZDO,ZNetView> Instances(this ZNetScene instance) => ref ZNetSceneInstances(instance);

    private static readonly AccessTools.FieldRef<ZNetView, IDictionary> ZNetViewRpcHandlers = AccessTools.FieldRefAccess<ZNetView, IDictionary>("m_functions");
    internal static ref IDictionary RpcHandlers(this ZNetView instance) => ref ZNetViewRpcHandlers(instance);

    private static readonly AccessTools.FieldRef<ZRoutedRpc, IDictionary> ZRoutedRpcRpcHandlers = AccessTools.FieldRefAccess<ZRoutedRpc, IDictionary>("m_functions");
    internal static ref IDictionary RpcHandlers(this ZRoutedRpc instance) => ref ZRoutedRpcRpcHandlers(instance);

    private static readonly AccessTools.FieldRef<ZRoutedRpc, long> ZRoutedRpcPeerId = AccessTools.FieldRefAccess<ZRoutedRpc, long>("m_id");
    internal static ref long PeerId(this ZRoutedRpc instance) => ref ZRoutedRpcPeerId(instance);

    private static readonly AccessTools.FieldRef<GameCamera, Camera> GameCameraGameCameraComponent = AccessTools.FieldRefAccess<GameCamera, Camera>("m_camera");
    internal static ref Camera GameCameraComponent(this GameCamera instance) => ref GameCameraGameCameraComponent(instance);

    private static readonly AccessTools.FieldRef<Localization, Dictionary<string,string>> LocalizationTranslations = AccessTools.FieldRefAccess<Localization, Dictionary<string,string>>("m_translations");
    internal static ref Dictionary<string,string> Translations(this Localization instance) => ref LocalizationTranslations(instance);

    private static readonly AccessTools.FieldRef<HotkeyBar, List<ItemDrop.ItemData>> HotkeyBarHotbarItems = AccessTools.FieldRefAccess<HotkeyBar, List<ItemDrop.ItemData>>("m_items");
    internal static ref List<ItemDrop.ItemData> HotbarItems(this HotkeyBar instance) => ref HotkeyBarHotbarItems(instance);

    private static readonly AccessTools.FieldRef<HotkeyBar, IList> HotkeyBarHotbarElements = AccessTools.FieldRefAccess<HotkeyBar, IList>("m_elements");
    internal static ref IList HotbarElements(this HotkeyBar instance) => ref HotkeyBarHotbarElements(instance);

    private static readonly Func<BaseAI, Vector3, bool> BaseAIHavePath = AccessTools.MethodDelegate<Func<BaseAI, Vector3, bool>>(AccessTools.DeclaredMethod(typeof(BaseAI), "HavePath", new Type[] { typeof(Vector3) }));
    internal static bool HavePath(this BaseAI instance, Vector3 target) => BaseAIHavePath(instance, target);

    private static readonly Action<Container> ContainerSave = AccessTools.MethodDelegate<Action<Container>>(AccessTools.DeclaredMethod(typeof(Container), "Save", new Type[] {  }));
    internal static void Save(this Container instance) => ContainerSave(instance);

    private static readonly Func<Growup, GameObject> GrowupGetPrefab = AccessTools.MethodDelegate<Func<Growup, GameObject>>(AccessTools.DeclaredMethod(typeof(Growup), "GetPrefab", new Type[] {  }));
    internal static GameObject GetPrefab(this Growup instance) => GrowupGetPrefab(instance);

    private static readonly Action<Humanoid, PieceTable> HumanoidSetPlaceMode = AccessTools.MethodDelegate<Action<Humanoid, PieceTable>>(AccessTools.DeclaredMethod(typeof(Humanoid), "SetPlaceMode", new Type[] { typeof(PieceTable) }));
    internal static void SetPlaceMode(this Humanoid instance, PieceTable buildPieces) => HumanoidSetPlaceMode(instance, buildPieces);

    private static readonly Action<Inventory, bool, bool> InventoryChanged = AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.DeclaredMethod(typeof(Inventory), "Changed", new Type[] { typeof(bool), typeof(bool) }));
    internal static void Changed(this Inventory instance, bool success = false, bool cheatedStateChanged = false) => InventoryChanged(instance, success, cheatedStateChanged);

    private static readonly Func<Procreation, bool> ProcreationIsPregnant = AccessTools.MethodDelegate<Func<Procreation, bool>>(AccessTools.DeclaredMethod(typeof(Procreation), "IsPregnant", new Type[] {  }));
    internal static bool IsPregnant(this Procreation instance) => ProcreationIsPregnant(instance);

    private static readonly Func<Sadle, bool> SadleIsLocalUser = AccessTools.MethodDelegate<Func<Sadle, bool>>(AccessTools.DeclaredMethod(typeof(Sadle), "IsLocalUser", new Type[] {  }));
    internal static bool IsLocalUser(this Sadle instance) => SadleIsLocalUser(instance);

    private static readonly Action<Sadle, float> SadleSetStamina = AccessTools.MethodDelegate<Action<Sadle, float>>(AccessTools.DeclaredMethod(typeof(Sadle), "SetStamina", new Type[] { typeof(float) }));
    internal static void SetStamina(this Sadle instance, float stamina) => SadleSetStamina(instance, stamina);

    private static readonly Action<Sadle, float> SadleUseStamina = AccessTools.MethodDelegate<Action<Sadle, float>>(AccessTools.DeclaredMethod(typeof(Sadle), "UseStamina", new Type[] { typeof(float) }));
    internal static void UseStamina(this Sadle instance, float v) => SadleUseStamina(instance, v);

    private static readonly Func<Tameable, bool> TameableHaveSaddle = AccessTools.MethodDelegate<Func<Tameable, bool>>(AccessTools.DeclaredMethod(typeof(Tameable), "HaveSaddle", new Type[] {  }));
    internal static bool HaveSaddle(this Tameable instance) => TameableHaveSaddle(instance);

    private static readonly Func<Tameable, float> TameableGetRemainingTime = AccessTools.MethodDelegate<Func<Tameable, float>>(AccessTools.DeclaredMethod(typeof(Tameable), "GetRemainingTime", new Type[] {  }));
    internal static float GetRemainingTime(this Tameable instance) => TameableGetRemainingTime(instance);

    private static readonly Action<Tameable, long, string, string> TameableRPC_SetName = AccessTools.MethodDelegate<Action<Tameable, long, string, string>>(AccessTools.DeclaredMethod(typeof(Tameable), "RPC_SetName", new Type[] { typeof(long), typeof(string), typeof(string) }));
    internal static void RPC_SetName(this Tameable instance, long sender, string name, string authorId) => TameableRPC_SetName(instance, sender, name, authorId);

    private static readonly Action<Tameable, bool> TameableSetSaddle = AccessTools.MethodDelegate<Action<Tameable, bool>>(AccessTools.DeclaredMethod(typeof(Tameable), "SetSaddle", new Type[] { typeof(bool) }));
    internal static void SetSaddle(this Tameable instance, bool enabled) => TameableSetSaddle(instance, enabled);

    private static readonly Action<Tameable> TameableTame = AccessTools.MethodDelegate<Action<Tameable>>(AccessTools.DeclaredMethod(typeof(Tameable), "Tame", new Type[] {  }));
    internal static void Tame(this Tameable instance) => TameableTame(instance);

    private static readonly Func<ZRoutedRpc, long> ZRoutedRpcGetServerPeerID = AccessTools.MethodDelegate<Func<ZRoutedRpc, long>>(AccessTools.DeclaredMethod(typeof(ZRoutedRpc), "GetServerPeerID", new Type[] {  }));
    internal static long GetServerPeerID(this ZRoutedRpc instance) => ZRoutedRpcGetServerPeerID(instance);

    private static readonly Action<Localization, string, string> LocalizationAddWord = AccessTools.MethodDelegate<Action<Localization, string, string>>(AccessTools.DeclaredMethod(typeof(Localization), "AddWord", new Type[] { typeof(string), typeof(string) }));
    internal static void AddWord(this Localization instance, string key, string text) => LocalizationAddWord(instance, key, text);

}
