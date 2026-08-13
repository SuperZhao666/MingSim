extends SceneTree

var failures: Array[String] = []

func _init() -> void:

	var scene := load("res://src/Ming.Godot/scenes/ui_preview.tscn") as PackedScene
	_assert(scene != null, "主场景可加载")
	if scene == null:
		_quit()
		return

	var root := scene.instantiate() as Control
	root.name = "AcceptanceRoot"
	root.set_size(Vector2(1600, 960))
	root.set_process_input(false)
	get_root().add_child(root)
	await process_frame
	await process_frame

	var map := root.get_node_or_null("MapView")
	_assert(map != null, "MapView 节点存在")
	if map != null:
		_assert(map.size.x > 0 and map.size.y > 0, "地图视口尺寸有效")
		_assert(map.LoadedFromManifest, "地图只读清单与两层纹理加载成功")
		_assert(map.PlaceCount == 6, "六个可玩节点全部来自 manifest")
		_assert(map.RouteCount >= 5, "路线全部来自 manifest")
		_assert(map.GeometryDepictDate == "1391-01-01", "保留 1391 近似几何日期")
		_assert(map.SnapshotDate == "1629-01-01", "保留 1629 快照日期")
		_assert(map.HistoricalWarning.contains("不能把本文件理解为完整1629版图"), "历史层警告明确 OPEN 与非完整势力图")
		map.SelectPlace("beijing")
		_assert(map.SelectedPlaceId == "beijing", "京师可选中")
		map.SelectPlace("tongzhou")
		_assert(map.SelectedPlaceId == "tongzhou", "通州可独立选中")
		var original_zoom: float = map.Zoom
		map.ToggleRoutes()
		_assert(not map.RoutesVisible, "路线层按钮可切换")
		map.ToggleHistoricalLayer()
		_assert(not map.HistoricalLayerVisible, "历史层按钮可切换")
		map.ResetView()
		_assert(is_equal_approx(map.Zoom, original_zoom), "地图缩放可复位")
		map.LoadManifest("res://assets/maps/generated/ming_1629_liaoxi/map-manifest.json")
		_assert(map.LoadedFromManifest, "辽西清单可切换")
		map.LoadManifest("res://missing-map-manifest.json")
		_assert(not map.LoadedFromManifest, "资源缺失时 fail-closed")

	_assert(root.get_node_or_null("MapView") != null, "地图容器保留")
	_assert(_find_label(root, "御案 · 待处理"), "御案标题存在")
	_assert(root.find_child("SimulationNotice", true, false) != null, "未接入 Simulation 的反馈控件存在")

	if failures.is_empty():
		print("UI_HEADLESS_ACCEPTANCE: PASS")
	else:
		for failure in failures:
			push_error("FAIL: " + failure)
		print("UI_HEADLESS_ACCEPTANCE: FAIL (%d)" % failures.size())
	_quit()

func _assert(condition: bool, message: String) -> void:
	if condition:
		print("PASS: " + message)
	else:
		failures.append(message)

func _find_label(root: Node, expected: String) -> bool:
	for child in root.find_children("*", "Label", true, false):
		if child is Label and child.text == expected:
			return true
	return false

func _quit() -> void:
	quit(0 if failures.is_empty() else 1)
