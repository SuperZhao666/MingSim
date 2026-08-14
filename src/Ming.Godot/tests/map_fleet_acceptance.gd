extends SceneTree
# M3 地图补齐 headless 验收（先写会失败的测试，再实现到通过）：
# 断言 MapView 只消费只读的 MapFleetReadModel（源文本不含 Ming.Domain / Ming.Simulation /
# WorldState 引用），并验证 6 个库存节点、5 条粮运路线、在途粮队插值与库存告急颜色的
# 公开只读表面。所有样本数值固定，插值只验证单调接近与不越界，避免依赖帧率。

const MAP_VIEW_SCRIPT := "res://src/Ming.Godot/scripts/MapView.cs"
const FLEET_READ_MODEL_SCRIPT := "res://src/Ming.Godot/scripts/ReadModels/MapFleetReadModel.cs"
const TEST_TIMEOUT_SECONDS := 30.0

var failures: Array[String] = []
var finished := false


func _init() -> void:
	create_timer(TEST_TIMEOUT_SECONDS).timeout.connect(_on_timeout)
	var scene := load("res://src/Ming.Godot/scenes/ui_preview.tscn") as PackedScene
	_assert(scene != null, "御案场景可加载")
	if scene == null:
		_finish()
		return

	var root := scene.instantiate() as Control
	root.name = "MapFleetAcceptanceRoot"
	root.set_size(Vector2(1600, 960))
	get_root().add_child(root)
	await process_frame
	await process_frame

	var map := root.get_node_or_null("MapView") as Control
	_assert(map != null, "MapView 节点存在")
	if map == null:
		root.queue_free()
		_finish()
		return
	_assert(map.has_method("SetFleetReadModel"), "MapView 暴露只读模型注入缝 SetFleetReadModel")
	_assert(map.has_method("InjectFleetAcceptanceSample"), "MapView 暴露验收样本注入缝")
	_assert(map.LoadedFromManifest, "注入前地图清单仍完整加载（不回归）")
	_assert(not map.FleetLayerVisible, "默认不显示粮运层")
	_assert(map.FleetStockpileCount == 0 and map.FleetRouteCount == 0 and map.FleetShipmentCount == 0,
		"默认无库存/路线/粮队呈现数据")

	_test_source_contract()
	_test_injected_sample(map)
	await _test_interpolation(map)
	await _test_delayed_target_does_not_slide_back(map)
	_test_reinject_keeps_progress(map)
	_test_existing_map_behavior_unaffected(map)

	root.queue_free()
	await process_frame
	_finish()


# MapView 只读契约：源文本不得引用 Ming.Domain / Ming.Simulation / WorldState，
# 只读模型 Presenter 位于 scripts/ReadModels 下。
func _test_source_contract() -> void:
	_assert(FileAccess.file_exists(FLEET_READ_MODEL_SCRIPT), "只读粮运模型 Presenter 文件存在")
	var source := FileAccess.get_file_as_string(MAP_VIEW_SCRIPT)
	_assert(not source.contains("using Ming.Domain"), "MapView 不含 Ming.Domain 引用")
	_assert(not source.contains("using Ming.Simulation"), "MapView 不含 Ming.Simulation 引用")
	_assert(not source.contains("WorldState.") and not source.contains("WorldState "),
		"MapView 不含 WorldState 引用")
	_assert(source.contains("SetFleetReadModel"), "MapView 源码包含只读模型注入方法")


# 注入 3 批样本后：6 库存 / 5 路线 / 3 粮队，告急颜色与目标进度全部来自固定样本。
func _test_injected_sample(map: Control) -> void:
	map.InjectFleetAcceptanceSample(3)
	_assert(map.FleetLayerVisible, "注入后粮运层可见")
	_assert(map.FleetStockpileCount == 6, "六个库存节点来自样本：%d" % map.FleetStockpileCount)
	_assert(map.FleetRouteCount == 5, "五条粮运路线来自样本：%d" % map.FleetRouteCount)
	_assert(map.FleetShipmentCount == 3, "三批粮队来自样本：%d" % map.FleetShipmentCount)
	_assert(map.FleetInTransitCount == 1, "在途粮队计数正确：%d" % map.FleetInTransitCount)
	_assert(map.FleetCriticalStockpileCount == 3, "告急（Critical）库存数正确：%d" % map.FleetCriticalStockpileCount)
	_assert(map.FleetWarningStockpileCount == 1, "预警（Warning）库存数正确：%d" % map.FleetWarningStockpileCount)
	_assert(map.FleetStockpileAlertLevel("sp-ningyuan") == "Warning", "宁远（前线）按日耗预警")
	_assert(map.FleetStockpileAlertLevel("sp-tongzhou") == "Critical", "零存粮转运节点告急")
	_assert(map.FleetStockpileAlertLevel("sp-beijing") == "Normal", "满仓中枢正常")
	_assert(absf(float(map.GetFleetShipmentTargetProgress(0)) - 0.6) < 0.0001,
		"在途样本目标进度 = 1 - 剩余48h/120h = 0.6")
	_assert(float(map.GetFleetShipmentTargetProgress(1)) == 1.0, "已抵达样本目标进度 = 1")
	_assert(float(map.GetFleetShipmentTargetProgress(2)) == 0.0, "计划样本目标进度 = 0")
	map.queue_redraw()
	await process_frame


# 插值：首次见到的在途粮队从起点（0）开始，随后单调向目标接近、不越界、最终收敛；
# 已抵达显示在终点，计划批停在起点。
func _test_interpolation(map: Control) -> void:
	var target: float = float(map.GetFleetShipmentTargetProgress(0))
	var first: float = float(map.GetFleetShipmentDisplayProgress(0))
	_assert(first == 0.0, "首次见到在途粮队从起点开始显示：%.3f" % first)
	await create_timer(0.8).timeout
	var mid: float = float(map.GetFleetShipmentDisplayProgress(0))
	_assert(mid > 0.0 and mid < target, "插值向目标单调接近且不越界：%.3f" % mid)
	await create_timer(1.5).timeout
	var late: float = float(map.GetFleetShipmentDisplayProgress(0))
	_assert(late >= mid, "插值进度不倒退：%.3f -> %.3f" % [mid, late])
	_assert(late < target + 0.0005, "插值不超过权威目标进度：%.3f" % late)
	_assert(absf(late - target) < 0.03, "插值最终收敛到目标：%.3f" % late)
	_assert(float(map.GetFleetShipmentDisplayProgress(1)) == 1.0, "已抵达粮队显示在终点")
	_assert(float(map.GetFleetShipmentDisplayProgress(2)) == 0.0, "计划粮队显示在起点")


# 天气延误把权威到货目标推后（0.6 -> 0.15）时，显示进度必须保持单调不倒退、不越界。
func _test_delayed_target_does_not_slide_back(map: Control) -> void:
	await create_timer(0.4).timeout
	var before: float = float(map.GetFleetShipmentDisplayProgress(0))
	_assert(before > 0.2, "延误区先让显示进度离开起点：%.3f" % before)
	map.InjectFleetAcceptanceSampleDelayed()
	_assert(float(map.GetFleetShipmentTargetProgress(0)) < before, "权威目标确实回落（延误区前提）：%.3f" % float(map.GetFleetShipmentTargetProgress(0)))
	await create_timer(0.8).timeout
	var after: float = float(map.GetFleetShipmentDisplayProgress(0))
	_assert(after >= before - 0.0001, "目标回落时显示进度不得倒退：%.3f -> %.3f" % [before, after])
	_assert(after < 1.0 + 0.0005, "显示进度不得越界：%.3f" % after)


# 重新注入同一世界的更新快照时，已见过的粮队显示进度不得重置回 0。
func _test_reinject_keeps_progress(map: Control) -> void:
	var before: float = float(map.GetFleetShipmentDisplayProgress(0))
	map.InjectFleetAcceptanceSample(1)
	var after: float = float(map.GetFleetShipmentDisplayProgress(0))
	_assert(after >= before, "重新注入快照不重置显示进度：%.3f -> %.3f" % [before, after])
	_assert(map.FleetShipmentCount == 1, "重新注入后粮队数量与样本一致")
	_assert(map.FleetStockpileCount == 6 and map.FleetRouteCount == 5, "库存与路线数量与样本一致")


# 粮运层注入不得干扰既有地图交互与语义表面。
func _test_existing_map_behavior_unaffected(map: Control) -> void:
	map.SelectPlace("ningyuan")
	_assert(str(map.SelectedPlaceId) == "ningyuan", "注入后点选节点仍沿原路径生效")
	_assert(str(map.SelectedPlaceSummary).contains("DESIGN/OPEN"), "注入后语义摘要不变")
	_assert(map.PlaceCount > 0 and map.RouteCount > 0, "注入后 manifest 节点/路线计数不变")


func _assert(condition: bool, message: String) -> void:
	if condition:
		print("PASS: " + message)
	else:
		failures.append(message)


func _on_timeout() -> void:
	if finished:
		return
	failures.append("粮运层 headless 验收超时")
	_finish()


func _finish() -> void:
	if finished:
		return
	finished = true
	if failures.is_empty():
		print("MAP_FLEET_ACCEPTANCE: PASS")
	else:
		for failure in failures:
			push_error("FAIL: " + failure)
		print("MAP_FLEET_ACCEPTANCE: FAIL (%d)" % failures.size())
	quit(0 if failures.is_empty() else 1)
