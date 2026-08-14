extends SceneTree

var failures: Array[String] = []

const EXPECTED_MAIN_UI_SCRIPT := "res://src/Ming.Godot/scripts/MainUi.cs"
const TEST_TIMEOUT_SECONDS := 15.0

var finished := false

func _init() -> void:
	create_timer(TEST_TIMEOUT_SECONDS).timeout.connect(_on_timeout)
	var scene := load("res://src/Ming.Godot/scenes/ui_preview.tscn") as PackedScene
	_assert(scene != null, "主场景可加载")
	if scene == null:
		_quit()
		return

	var root := scene.instantiate() as Control
	root.name = "AcceptanceRoot"
	root.set_size(Vector2(1600, 960))
	get_root().add_child(root)
	await process_frame
	await process_frame
	var attached_script: Script = root.get_script()
	_assert(attached_script != null \
		and attached_script.resource_path == EXPECTED_MAIN_UI_SCRIPT \
		and attached_script.get_class() == "CSharpScript" \
		and root.has_method("SetStrategicView"), "主场景实例化真实 C# MainUi，而不是无脚本背景壳")
	if attached_script == null or not root.has_method("SetStrategicView"):
		_quit()
		return

	var map := root.get_node_or_null("MapView")
	_assert(map != null, "MapView 节点存在")
	if map != null:
		_assert(map.size.x > 0 and map.size.y > 0, "地图视口尺寸有效")
		_assert(map.LoadedFromManifest, "地图只读清单与纹理加载成功")
		_assert(map.PlaceCount > 0, "节点数量来自当前 manifest，未写死数量")
		_assert(map.RouteCount > 0, "路线数量来自当前 manifest，未写死下限")
		_assert(map.GeometryDepictDate != "OPEN", "几何日期由契约提供")
		_assert(map.SnapshotDate != "OPEN", "快照日期由契约提供")
		_assert(map.HistoricalWarning != "", "历史语义警告由契约提供")
		_assert(not map.IsStrategicView, "地图默认处于御案桌面总览态")
		_assert(is_equal_approx(map.MinimumZoom, 1.0) and is_equal_approx(map.MaximumZoom, 1.0), "桌面态缩放固定为 1")
		_assert(map.VisibleLabelCount == 0, "桌面态不显示任何城镇名称")
		_assert(map.VisiblePlaceCount < map.PlaceCount, "最远层级只保留少量战略节点")
		_assert(_content_covers_viewport(map), "桌面态清单内容覆盖视口四边")
		var desk_pan_before: Vector2 = map.Pan
		_send_drag(map, Vector2(map.size.x * 0.5, map.size.y * 0.5), Vector2(10000, 10000))
		_assert(map.Pan.is_equal_approx(desk_pan_before), "桌面舆图不会被拖离御案")
		_send_wheel(map, MOUSE_BUTTON_WHEEL_UP, Vector2(map.size.x * 0.5, map.size.y * 0.5))
		_assert(is_equal_approx(map.Zoom, 1.0), "桌面舆图把滚轮转场决定留给 MainUi")

		var selected_before: String = str(map.SelectedPlaceId)
		var manifest_before: String = str(map.ManifestPath)
		var routes_before: bool = map.RoutesVisible
		var history_before: bool = map.HistoricalLayerVisible
		var point: Vector2 = map.GetViewportPointForPlace(selected_before)
		_send_mouse(map, InputEventMouseButton.new(), MOUSE_BUTTON_LEFT, point, true)
		_send_mouse(map, InputEventMouseButton.new(), MOUSE_BUTTON_LEFT, point, false)
		_assert(map.SelectedPlaceId == selected_before, "鼠标点选沿 _GuiInput 路径保持节点选择")

		map.EnterStrategicView()
		_assert(map.IsStrategicView, "EnterStrategicView 进入全屏策略地图态")
		_assert(is_equal_approx(map.MinimumZoom, 1.0) and is_equal_approx(map.MaximumZoom, 4.0), "全屏策略地图支持 1..4 缩放")
		_assert(is_equal_approx(map.Zoom, 1.0) and map.VisibleLabelCount == 0, "战略态最远层级仍不显示地名")
		var zoom_before: float = map.Zoom
		_send_wheel(map, MOUSE_BUTTON_WHEEL_UP, Vector2(map.size.x * 0.5, map.size.y * 0.5))
		_assert(map.Zoom > zoom_before, "滚轮缩放沿 _GuiInput 路径生效")
		for step in range(2):
			_send_wheel(map, MOUSE_BUTTON_WHEEL_UP, Vector2(map.size.x * 0.5, map.size.y * 0.5))
		_assert(map.VisibleLabelCount > 0 and map.VisibleLabelCount < map.PlaceCount, "中层级只显示高优先级地名")
		for step in range(20):
			_send_wheel(map, MOUSE_BUTTON_WHEEL_UP, Vector2(map.size.x * 0.5, map.size.y * 0.5))
		_assert(is_equal_approx(map.Zoom, 4.0), "战略态近层级缩放上限为 4")
		_assert(map.VisibleLabelCount > 0 and map.VisibleLabelCount <= map.VisiblePlaceCount, "近层级标签数是视口裁剪和碰撞过滤后的实际绘制数")
		var pan_before: Vector2 = map.Pan
		_send_drag(map, Vector2(map.size.x * 0.5, map.size.y * 0.5), Vector2(10000, 10000))
		_assert(map.Pan != pan_before, "拖动沿 _GuiInput 路径生效")
		_assert(_content_covers_viewport(map), "极限拖动后清单内容仍覆盖视口四边")
		var east_clamp: Vector2 = map.Pan
		_send_drag(map, Vector2(map.size.x * 0.5, map.size.y * 0.5), Vector2(10000, 0))
		_assert(map.Pan.is_equal_approx(east_clamp), "继续向边界外拖动不会露出清单外蓝海")
		_assert(_content_covers_viewport(map), "重复越界拖动仍不暴露清单外区域")
		map.ResetView()
		_assert(is_equal_approx(map.Zoom, 1.0), "真实缩放后 Reset 恢复视图")
		_send_wheel(map, MOUSE_BUTTON_WHEEL_DOWN, Vector2(map.size.x * 0.5, map.size.y * 0.5))
		_assert(is_equal_approx(map.Zoom, 1.0), "最远视图不能缩到内容范围之外")
		map.ExitStrategicView()
		_assert(not map.IsStrategicView and is_equal_approx(map.Zoom, 1.0), "ExitStrategicView 回到桌面总览态")
		_assert(map.Pan.is_equal_approx(Vector2.ZERO) and map.VisibleLabelCount == 0, "退出全屏后清除平移并隐藏地名")
		_assert(map.ManifestPath == manifest_before and map.SelectedPlaceId == selected_before, "模式切换不改清单与选择状态")
		_assert(map.RoutesVisible == routes_before and map.HistoricalLayerVisible == history_before, "模式切换不改地图图层状态")
		_assert(_map_view_has_no_world_state_dependency(), "MapView 模式切换没有 WorldState 写入依赖")
		_assert(_mode_methods_only_write_presentation_fields(), "进入/退出模式只写 MapView 呈现字段")

		map.ToggleRoutes()
		_assert(not map.RoutesVisible, "路线层可切换")
		map.ToggleHistoricalLayer()
		_assert(not map.HistoricalLayerVisible, "历史层可切换")
		map.LoadManifest("res://assets/maps/generated/ming_1629_liaoxi/map-manifest.json")
		_assert(map.LoadedFromManifest, "辽西清单可切换")
		map.LoadManifest("res://missing-map-manifest.json")
		_assert(not map.LoadedFromManifest, "资源缺失时 fail-closed")
		_assert(map.LoadError != "", "坏清单显示中性错误语义")

	_assert(root.get_node_or_null("MapView") != null, "地图容器保留")
	_assert(root.find_child("SimulationNotice", true, false) != null, "未接入 Simulation 的反馈控件存在")
	_assert(root.find_child("DeskLayer", true, false) != null, "御案桌面层存在")
	var memorials := root.find_children("DeskMemorial-*", "TextureButton", true, false)
	_assert(memorials.size() == root.PendingMemorialCount, "桌面奏疏实体数量与待办队列一致")
	if not memorials.is_empty():
		(memorials[0] as BaseButton).emit_signal("pressed")
		await process_frame
		_assert(root.MemorialOpen, "点击桌面奏疏可翻阅对应奏报")
	var desk_map := root.find_child("DeskMapScroll", true, false) as BaseButton
	_assert(desk_map != null, "桌面舆图入口存在")
	if desk_map != null:
		desk_map.emit_signal("pressed")
		_assert(await _wait_for_transition(root), "御案到舆图的显示转场在时限内完成")
		_assert(root.StrategicView and map.IsStrategicView, "点击桌面舆图进入全屏战略地图")
		root.call("SetStrategicView", false)
		_assert(await _wait_for_transition(root), "舆图收卷回御案的显示转场在时限内完成")
		_assert(not root.StrategicView and not map.IsStrategicView, "收卷后返回御案并重置地图呈现态")
	_assert(root.find_child("StrategicMapLayer", true, false) != null, "全屏策略地图层存在")
	_assert(root.find_child("ReturnToDesk", true, false) != null, "全屏地图保留收卷归案入口")
	_assert(root.find_child("ConfirmSealButton", true, false) != null, "朱批确认控件提供真实交互状态")

	if failures.is_empty():
		print("UI_HEADLESS_ACCEPTANCE: PASS")
	else:
		for failure in failures:
			push_error("FAIL: " + failure)
		print("UI_HEADLESS_ACCEPTANCE: FAIL (%d)" % failures.size())
	_quit()

func _send_mouse(map: Control, event: InputEventMouseButton, button: int, position: Vector2, pressed: bool) -> void:
	event.button_index = button
	event.position = position
	event.pressed = pressed
	map._gui_input(event)

func _send_wheel(map: Control, button: int, position: Vector2) -> void:
	var event := InputEventMouseButton.new()
	event.button_index = button
	event.position = position
	event.pressed = true
	map._gui_input(event)

func _send_drag(map: Control, start: Vector2, delta: Vector2) -> void:
	_send_mouse(map, InputEventMouseButton.new(), MOUSE_BUTTON_LEFT, start, true)
	var motion := InputEventMouseMotion.new()
	motion.position = start + delta
	motion.relative = delta
	map._gui_input(motion)
	_send_mouse(map, InputEventMouseButton.new(), MOUSE_BUTTON_LEFT, start + delta, false)

func _content_covers_viewport(map: Control) -> bool:
	var content: Rect2 = map.ContentViewportRect
	var epsilon := 0.1
	return content.position.x <= epsilon \
		and content.position.y <= epsilon \
		and content.end.x >= map.size.x - epsilon \
		and content.end.y >= map.size.y - epsilon

func _map_view_has_no_world_state_dependency() -> bool:
	var source := FileAccess.get_file_as_string("res://src/Ming.Godot/scripts/MapView.cs")
	return not source.contains("using Ming.Domain") \
		and not source.contains("using Ming.Simulation") \
		and not source.contains("WorldState.") \
		and not source.contains("WorldState ")

func _mode_methods_only_write_presentation_fields() -> bool:
	var source := FileAccess.get_file_as_string("res://src/Ming.Godot/scripts/MapView.cs")
	var enter_body := _method_body(source, "public void EnterStrategicView()", "public void ExitStrategicView()")
	var exit_body := _method_body(source, "public void ExitStrategicView()", "public override void _GuiInput")
	var forbidden := ["LoadManifest(", "SelectPlace(", "ToggleRoutes(", "ToggleHistoricalLayer(", "WorldState", "Commit(", "ResolveTurn("]
	if enter_body.is_empty() or exit_body.is_empty():
		return false
	for token in forbidden:
		if enter_body.contains(token) or exit_body.contains(token):
			return false
	return true

func _method_body(source: String, start_marker: String, end_marker: String) -> String:
	var start := source.find(start_marker)
	if start < 0:
		return ""
	var end := source.find(end_marker, start + start_marker.length())
	if end < 0:
		return ""
	return source.substr(start, end - start)

func _assert(condition: bool, message: String) -> void:
	if condition:
		print("PASS: " + message)
	else:
		failures.append(message)

func _quit() -> void:
	if finished:
		return
	finished = true
	quit(0 if failures.is_empty() else 1)

func _wait_for_transition(root: Control, timeout_seconds := 2.0) -> bool:
	var elapsed := 0.0
	while bool(root.get("Transitioning")) and elapsed < timeout_seconds:
		await create_timer(0.02).timeout
		elapsed += 0.02
	return not bool(root.get("Transitioning"))

func _on_timeout() -> void:
	if finished:
		return
	failures.append("UI headless acceptance 超时")
	_quit()
