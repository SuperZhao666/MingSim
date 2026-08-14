extends SceneTree
# 真实 Viewport 输入分发验收：所有事件都经 get_root().push_input() 走完整的
# SceneTree/Viewport GUI 拾取链，而不是直接调用 map._gui_input() 或 emit_signal。
# 覆盖：桌面滚轮经真实路由进入舆图、战略态点击/拖拽、转场输入锁与遮挡层。

const SCENE_PATH := "res://src/Ming.Godot/scenes/ui_preview.tscn"
const EXPECTED_SCRIPT_PATH := "res://src/Ming.Godot/scripts/MainUi.cs"
const TEST_TIMEOUT_SECONDS := 20.0

var failures: Array[String] = []
var finished := false


func _init() -> void:
	create_timer(TEST_TIMEOUT_SECONDS).timeout.connect(_on_timeout)
	var scene := load(SCENE_PATH) as PackedScene
	_assert(scene != null, "御案场景可加载")
	if scene == null:
		_finish()
		return

	var root := scene.instantiate() as Control
	_assert(root != null, "御案场景可实例化")
	if root == null:
		_finish()
		return

	root.name = "ViewportInputAcceptanceRoot"
	get_root().size = Vector2i(1600, 960)
	root.set_size(Vector2(1600, 960))
	get_root().add_child(root)
	await process_frame
	await process_frame

	var attached_script: Script = root.get_script()
	var real_main_ui := attached_script != null \
		and attached_script.resource_path == EXPECTED_SCRIPT_PATH \
		and attached_script.get_class() == "CSharpScript" \
		and root.has_method("SetStrategicView")
	_assert(real_main_ui, "根节点是真实 C# MainUi")
	if not real_main_ui:
		_finish()
		return

	var map := root.get_node_or_null("MapView") as Control
	var blocker := root.find_child("TransitionInputBlocker", true, false) as Control
	_assert(map != null and blocker != null, "地图与输入阻断层真实存在")
	if map == null or blocker == null:
		_finish()
		return
	_assert(map.LoadedFromManifest, "真实 MapView 加载合法清单")

	# 1) 桌面态：滚轮事件经 Viewport 路由，由案上舆图按钮进入全屏战略态
	_assert(not bool(root.get("StrategicView")), "初始处于御案桌面态")
	_push_wheel(get_root(), Vector2(800, 560), MOUSE_BUTTON_WHEEL_UP)
	await process_frame
	_assert(bool(root.get("StrategicView")) and bool(root.get("Transitioning")),
		"桌面滚轮经真实 Viewport 路由进入舆图转场")
	_assert(blocker.visible and blocker.mouse_filter == Control.MOUSE_FILTER_STOP,
		"转场期间遮挡层真实接管鼠标")
	_assert(await _wait_for_transition(root, 2.0), "进入舆图转场在时限内完成")
	_assert(not bool(root.get("InputLocked")), "进入完成后解除输入锁")
	_assert(map.mouse_filter == Control.MOUSE_FILTER_STOP, "战略态地图真实接收鼠标")

	# 2) 战略态：点击经 Viewport 路由选择节点（坐标=地图本地点+地图屏幕位置）
	# 先直接选中宁远再点击北京，证明选择确实来自点击而不是默认选中值。
	map.SelectPlace("ningyuan")
	_assert(str(map.SelectedPlaceId) == "ningyuan", "前置选中宁远作为点击对照")
	var beijing_point: Vector2 = map.GetViewportPointForPlace("beijing")
	_assert(Rect2(Vector2.ZERO, map.size).has_point(beijing_point), "北京点落在真实视口内")
	_push_click(get_root(), beijing_point + map.position)
	await process_frame
	_assert(str(map.SelectedPlaceId) == "beijing", "真实 Viewport 路由点击选择北京")

	# 3) 战略态：先经 Viewport 滚轮放大（远层级 cover 恰满视口，平移必然被 clamp），
	# 再拖拽平移——证明滚轮与拖拽都走真实 Viewport 路由。
	var zoom_before: float = map.Zoom
	for _step in range(8):
		_push_wheel(get_root(), Vector2(map.size.x * 0.5, map.size.y * 0.5), MOUSE_BUTTON_WHEEL_UP)
		await process_frame
	_assert(map.Zoom > zoom_before, "真实 Viewport 路由滚轮缩放生效")
	var pan_before: Vector2 = map.Pan
	_push_drag(get_root(), Vector2(900, 500), Vector2(300, 0))
	await process_frame
	_assert(map.Pan != pan_before, "真实 Viewport 路由拖拽平移生效")

	# 4) 转场锁：收卷转场期间推入的点击必须被遮挡层拦截，地图选择不变
	root.call("SetStrategicView", false)
	_assert(bool(root.get("InputLocked")) and bool(root.get("Transitioning")),
		"收卷转场开始即锁输入")
	var selection_before: String = str(map.SelectedPlaceId)
	_push_click(get_root(), beijing_point + map.position)
	await process_frame
	_assert(str(map.SelectedPlaceId) == selection_before, "转场期间真实输入被阻断层拦截")
	_assert(await _wait_for_transition(root, 2.0), "收卷归案转场在时限内完成")
	_assert(not bool(root.get("InputLocked")), "收卷完成后解除输入锁")

	_finish()


func _push_click(viewport: Viewport, position: Vector2) -> void:
	var down := InputEventMouseButton.new()
	down.button_index = MOUSE_BUTTON_LEFT
	down.position = position
	down.pressed = true
	viewport.push_input(down)
	var up := InputEventMouseButton.new()
	up.button_index = MOUSE_BUTTON_LEFT
	up.position = position
	up.pressed = false
	viewport.push_input(up)


func _push_drag(viewport: Viewport, start: Vector2, delta: Vector2) -> void:
	var down := InputEventMouseButton.new()
	down.button_index = MOUSE_BUTTON_LEFT
	down.position = start
	down.pressed = true
	viewport.push_input(down)
	var motion := InputEventMouseMotion.new()
	motion.position = start + delta
	motion.relative = delta
	viewport.push_input(motion)
	var up := InputEventMouseButton.new()
	up.button_index = MOUSE_BUTTON_LEFT
	up.position = start + delta
	up.pressed = false
	viewport.push_input(up)


func _push_wheel(viewport: Viewport, position: Vector2, button: MouseButton) -> void:
	var event := InputEventMouseButton.new()
	event.button_index = button
	event.position = position
	event.pressed = true
	viewport.push_input(event)


func _wait_for_transition(root: Control, timeout_seconds: float) -> bool:
	var elapsed := 0.0
	while bool(root.get("Transitioning")) and elapsed < timeout_seconds:
		await create_timer(0.02).timeout
		elapsed += 0.02
	return not bool(root.get("Transitioning"))


func _assert(condition: bool, message: String) -> void:
	if condition:
		print("PASS: " + message)
	else:
		failures.append(message)


func _on_timeout() -> void:
	if finished:
		return
	failures.append("Viewport 输入分发验收超时")
	_finish()


func _finish() -> void:
	if finished:
		return
	finished = true
	if failures.is_empty():
		print("VIEWPORT_INPUT_ACCEPTANCE: PASS")
	else:
		for failure in failures:
			push_error("FAIL: " + failure)
		print("VIEWPORT_INPUT_ACCEPTANCE: FAIL (%d)" % failures.size())
	quit(0 if failures.is_empty() else 1)
