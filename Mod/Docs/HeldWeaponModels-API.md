# FPS held weapon model API v3

R2增加第一人称独立描边、光学观察点和初始倍率；R3增加专用手持模型Def，R3.1明确原OBJ文件与加载模型的坐标转换。`scope="Auto"` 为新默认；给出 `scopeEye` 时可根据武器规则选镜型，显式 `None` 保留机瞄/准心。原注册接口兼容，`Version` 当前为3。详细改变和验证边界见R4-Changes.md。

XML光学配置示例（这些坐标必须换成自己的原始模型坐标）：

```xml
<hasScopeEye>true</hasScopeEye>
<scopeEye>(-0.1, 0.29, 0)</scopeEye>
<scopeForward>(1, 0, 0)</scopeForward>
<scope>ModernPrecision</scope>
<scopeZoom>4</scopeZoom>
```

C#提供者可用 `scopeEye`、`scopeForward`、`scopeZoom` 同名字段。scopeForward是方向而不是终点；省略时使用模型forward。完整模型/材质仍复用IsoRim，无需修改模型包依赖。FPS自己的描边开关默认关闭，世界描边仍由IsoRim管理。

XML/专用Def坐标默认来自原OBJ文件（rawObjCoordinates=true），入口将Z映射到ObjLoader已加载的引擎坐标一次；不会再次翻转模型。C#提供者默认使用已加载Model坐标，若提供原OBJ文件点请显式设置rawObjCoordinates=true。自动推导的engine点不再映射。

本接口只选择和绘制第一人称眼前的模型；弹药、伤害、散布、射击模式、冷却继续由武器和原版/CE控制。


## v3 dedicated eye-level model definition

A model pack may optionally add `HSK.FpsBridge.HeldWeaponVisualDef` while FPS is active. `weapon` is a ThingDef reference; `model` is relative to the declaring mod's Models folder, with no .obj suffix; `view` accepts the HeldWeaponModelExtension calibration fields. Keep such definitions in a conditional FPS compatibility folder if FPS is optional. This does not register a world ModelDef and does not change dropped or third-person art.

```xml
<HSK.FpsBridge.HeldWeaponVisualDef MayRequire="hsk.fpsbridge.heldmodels.r4">
  <defName>Example_Rifle_Held</defName>
  <weapon>Example_Rifle</weapon>
  <model>Example/Rifle_held</model>
  <view>
    <forward>(1, 0, 0)</forward><up>(0, 1, 0)</up>
    <hasGrip>true</hasGrip><grip>(0, 0, 0)</grip>
    <hasSights>true</hasSights>
    <rearSight>(-0.1, 0.2, 0)</rearSight><frontSight>(0.5, 0.2, 0)</frontSight>
    <scope>None</scope><kind>Gun</kind>
  </view>
</HSK.FpsBridge.HeldWeaponVisualDef>
```

The sample coordinates are illustrative. Use actual aperture/notch and tip points, preserve original raw coordinates, and confirm the view from the final OBJ. Registered C# providers still take priority and the v1/v2 registration signatures remain compatible. API version is now 3.

## 自动使用已有模型

新武器按 IsoRim 正常方式注册 `IsoRim.ModelDef` 后，FPS 会尝试 `WeaponModels.ModelOf(weapon)`，沿用 IsoRim 选中的款式/外观和材质。仅有关键词相似、缺失模型、平面卡或加载失败时保留 FPS 原回退逻辑。原有 FPS 专用 ImportedWeaponDef 与显式 modelId 优先；核心原版武器已有校准机瞄时，保留该校准。外部模型包的精确绑定优先于通用 FPS 模型。

使用的是 IsoRim 已载入的 Model，不重新转换 OBJ、不简化面数、不复制贴图。纹理/光束和可选原描边继续复用 IsoRim.WeaponModels.Draw；纯色手持模型使用独立视图批次及稳定明暗，保留部件颜色和发光，黑铁细节提亮按角色保留近黑材质的相对明暗。第一人称姿态只施加旋转、平移和统一比例；相机的高度压缩补偿与既有 FPS 一致。

## XML 校准（推荐静态资源包）

在武器 ThingDef 的 modExtensions 中增加以下项；坐标都使用原始模型坐标。示例假设模型向 +X 开火、+Y 向上。不要把示例坐标直接套用到其他网格。

```xml
<li Class="HSK.FpsBridge.HeldWeaponModelExtension">
  <overrideOrientation>true</overrideOrientation>
  <forward>(1, 0, 0)</forward>
  <up>(0, 1, 0)</up>
  <hasGrip>true</hasGrip>
  <grip>(0, 0, 0)</grip>
  <hasMuzzle>true</hasMuzzle>
  <muzzle>(0.65, 0.13, 0)</muzzle>
  <hasSights>true</hasSights>
  <rearSight>(-0.1, 0.25, 0)</rearSight>
  <frontSight>(0.5, 0.25, 0)</frontSight>
  <kind>Gun</kind>
  <scope>None</scope>
  <rearDistance>0.22</rearDistance>
  <magnification>1.35</magnification>
</li>
```

`kind` 可为 Gun、Blade、Grenade、Bow、Launcher、Mounted、Spotter；它只选择视觉动作。真实近战武器强制使用 Blade。`scope` 使用 FPS ScopeStyle 的名称，None 表示不强制光学镜；提供成对有效瞄具点会启用实体机瞄。`disableAutomatic=true` 可让单件武器退回原 FPS 视觉选择。无校准时使用 IsoRim 的拟合朝向和自动握持位置，特殊武器建议显式标注。

独立模型包若不要求 FPS 为前置，不要直接无条件声明上述 Class。把校准放进仅在 FPS 启用时载入的 LoadFolders/兼容补丁，或由 FPS 侧附加补丁。这个候选的 CE Armory 校准放在 FPS 模组内，原模型包没有增加 FPS 依赖。

## C# 提供者（动态外观）

引用 HSK.FpsBridge.dll、IsoRim.dll 和游戏常规引用，在主线程注册。提供者只处理自己认识的武器，其他武器返回 null。

```csharp
HeldWeaponModelsApi.Register("example.myweaponpack", weapon => {
    if (weapon.def.defName != "Example_Rifle") return null;
    Model own = WeaponModels.ModelOf(weapon);
    if (own == null) return null;
    return new HeldWeaponModel {
        id = "Example_Rifle_FPS",
        model = own,
        forward = Vector3.right,
        up = Vector3.up,
        grip = Vector3.zero,
        muzzle = new Vector3(.65f, .13f, 0),
        rearSight = new Vector3(-.1f, .25f, 0),
        frontSight = new Vector3(.5f, .25f, 0)
    };
});
```

`HeldWeaponModelsApi.Version` 当前为 3。后注册者优先，同 owner 重新注册会替换旧提供者。`Unregister(owner)` 移除自己；动态网格/外观在 weapon、Graphic、Style 未改变时，调用 `Invalidate()` 使持枪缓存刷新。不要每帧注册或使缓存失效。提供者返回已加载的Model，FPS不负责释放外部Model，也不修改其顶点或材质。异常会记录并继续查找提供者/自动模型；无效几何安全回退。

## 接入边界与验证

这次针对的是 FPS 眼前通用枪替换。第三人称平面图残留由模型包已有 HeldWeaponsCE 处理，是另一条调用链。掉落物、UI、2D 模式不由本接口绘制。

R4测试证据以Evidence/acceptance.json为准。前版R3.1回执保留在原候选。真实键鼠、持续战斗和独立无CE进档仍需分别确认。

公开复用来源：https://github.com/peren717/IsoRim-1.7 。使用现有Model、OBJ加载和材质接口；R3专用外观数据不替换世界绑定。
