extends SceneTree

const SCENE_PATH := "res://src/Ming.Godot/scenes/ui_preview.tscn"
const EXPECTED_SCRIPT_PATH := "res://src/Ming.Godot/scripts/MainUi.cs"
const TEST_TIMEOUT_SECONDS := 12.0

var failures: Array[String] = []
var finished := false

func _init() -> void:
	var watchdog := create_timer(TEST_TIMEOUT_SECONDS)
	watchdog.timeout.connect(_on_timeout)

	var scene := load(SCENE_PATH) as PackedScene
	_assert(scene != null, "御案场景资源可加载")
	if scene == null:
		_finish()
		return

	var root := scene.instantiate() as Control
	_assert(root != null, "御案场景可以实例化为 Control")
	if root == null:
		_finish()
		return

	root.name = "MainUiTransitionAcceptanceRoot"
	root.set_size(Vector2(1600, 960))
	get_root().add_child(root)
	await process_frame
	await process_frame

	var attached_script: Script = root.get_script()
	var real_main_ui := attached_script != null \
		and attached_script.resource_path == EXPECTED_SCRIPT_PATH \
		and attached_script.get_class() == "CSharpScript" \
		and root.has_method("SetStrategicView") \
		and root.has_method("SetReadModel") \
		and root.has_method("InjectAcceptanceReadModel")
	_assert(real_main_ui, "根节点是真实 C# MainUi，关键方法已绑定")
	if not real_main_ui:
		_finish()
		return

	var map := root.get_node_or_null("MapView") as Control
	var desk := root.find_child("DeskLayer", true, false) as Control
	var map_layer := root.find_child("StrategicMapLayer", true, false) as Control
	var blocker := root.find_child("TransitionInputBlocker", true, false) as Control
	var background := root.find_child("StudyBackground", true, false) as TextureRect
	_assert(map != null and desk != null and map_layer != null and blocker != null, "关键御案、舆图与输入锁节点真实存在")
	_assert(background != null and background.texture != null and background.visible, "运行态不是纯空壳：御书房背景纹理真实加载")
	_assert(desk != null and desk.get_child_count() >= 8 and desk.visible, "御案层包含非空的可呈现控件树")
	_assert(str(root.get("ReadModelClassification")) == "DESIGN", "默认只读模型明确标记 DESIGN")
	var background_provenance := root.find_child("BackgroundProvenance", true, false) as Label
	_assert(background_provenance != null and "DESIGN" in background_provenance.text and "艺术合成背景" in background_provenance.text, "背景在运行时明确标记为 DESIGN 艺术合成")
	_assert(str(root.get("ReadModelSourceNotice")).contains("尚未接入真实 Simulation"), "默认只读模型明确尚未接入真实 Simulation")
	if map == null or desk == null or map_layer == null or blocker == null:
		_finish()
		return

	var memorial_sheet := root.find_child("MemorialSheet", true, false) as Panel
	var decree_sheet := root.find_child("EdictConfirmPanel", true, false) as Panel
	var paper_style := memorial_sheet.get_theme_stylebox("panel") as StyleBoxTexture if memorial_sheet != null else null
	_assert(paper_style != null, "奏疏正文使用真实 StyleBoxTexture 纸页")
	if paper_style != null and memorial_sheet != null and decree_sheet != null:
		var horizontal_margins := paper_style.get_texture_margin(SIDE_LEFT) + paper_style.get_texture_margin(SIDE_RIGHT)
		var vertical_margins := paper_style.get_texture_margin(SIDE_TOP) + paper_style.get_texture_margin(SIDE_BOTTOM)
		_assert(horizontal_margins < memorial_sheet.size.x and horizontal_margins < decree_sheet.size.x, "NinePatch 横向安全边距适配 696 与 652 两种宽度")
		_assert(vertical_margins < memorial_sheet.size.y and vertical_margins < decree_sheet.size.y, "NinePatch 纵向安全边距适配 610 与 570 两种高度")
	_assert(root.find_child("SelectedPlaceStatusBadge", true, false) is TextureRect, "地图选中语义实际消费 OPEN/FACT 状态徽章纹理")

	for count in [0, 1, 5]:
		root.call("InjectAcceptanceReadModel", count)
		await process_frame
		var memorials := root.find_children("DeskMemorial-*", "TextureButton", true, false)
		_assert(int(root.get("PendingMemorialCount")) == count, "只读列表成功注入 %d 条待办" % count)
		_assert(int(root.get("RenderedMemorialCount")) == count, "桌面奏疏实体数等于只读列表数：%d" % count)
		_assert(memorials.size() == count, "真实 TextureButton 数量等于只读列表数：%d" % count)

	root.call("InjectAcceptanceReadModel", 1)
	await process_frame
	var one_memorial := root.find_child("DeskMemorial-1", true, false) as BaseButton
	_assert(one_memorial != null, "单条注入生成可点击奏疏")
	if one_memorial != null:
		var status_badge := one_memorial.find_child("StatusBadge-DESIGN", true, false) as TextureRect
		_assert(status_badge != null and status_badge.texture != null, "奏疏 DESIGN 状态实际消费徽章纹理")
		one_memorial.emit_signal("pressed")
		await process_frame
		_assert(bool(root.get("MemorialOpen")), "注入后的奏疏仍可点击翻阅")

	var desk_rect := Rect2(map.position, map.size)
	root.call("SetStrategicView", true)
	_assert(bool(root.get("StrategicView")), "进入转场时目标状态立即成为策略舆图")
	_assert(bool(root.get("Transitioning")) and bool(root.get("InputLocked")), "转场期间真实锁住输入")
	_assert(blocker.visible and blocker.mouse_filter == Control.MOUSE_FILTER_STOP, "透明输入阻断层实际接管鼠标")

	# headless 帧率不稳定（可能只有几帧/秒），不能用固定 0.12s 恰好采到中间帧；
	# 改为轮询捕捉真正处于中间态的样本，转场确实连续放大才算通过。
	var enter_progress := 0.0
	var enter_mid_rect := Rect2(map.position, map.size)
	var observed_enter_mid := false
	var enter_elapsed := 0.0
	while bool(root.get("Transitioning")) and enter_elapsed < 1.0:
		enter_progress = float(root.get("TransitionProgress"))
		enter_mid_rect = Rect2(map.position, map.size)
		if enter_progress > 0.02 and enter_progress < 0.98 \
				and not enter_mid_rect.is_equal_approx(desk_rect) \
				and enter_mid_rect.size.x > desk_rect.size.x \
				and enter_mid_rect.size.y > desk_rect.size.y:
			observed_enter_mid = true
			break
		await create_timer(0.02).timeout
		enter_elapsed += 0.02
	_assert(observed_enter_mid, "进入舆图存在可观测的连续中间进度")
	_assert(enter_progress > 0.0 and enter_progress < 1.0, "进入中间帧进度位于 0..1 之间")
	_assert(not enter_mid_rect.is_equal_approx(desk_rect), "进入中间帧已离开桌面舆图矩形")
	_assert(enter_mid_rect.size.x > desk_rect.size.x and enter_mid_rect.size.y > desk_rect.size.y, "进入中间帧连续放大舆图")
	_assert(bool(root.get("InputLocked")), "进入中间帧仍保持输入锁")
	_assert(await _wait_for_transition(root, 2.0), "进入舆图转场在时限内完成")
	var strategic_rect := Rect2(map.position, map.size)
	_assert(not bool(root.get("InputLocked")) and not bool(root.get("Transitioning")), "进入完成后解除输入锁")
	_assert(map.visible and map_layer.visible and not desk.visible, "进入完成后显示真实策略舆图并收起御案")
	_assert(strategic_rect.size.x > enter_mid_rect.size.x and strategic_rect.size.y > enter_mid_rect.size.y, "进入末帧完成连续放大")

	root.call("SetStrategicView", false)
	_assert(not bool(root.get("StrategicView")) and bool(root.get("InputLocked")), "收卷开始时目标回到御案且锁住输入")
	var exit_progress := 0.0
	var exit_mid_rect := Rect2(map.position, map.size)
	var observed_exit_mid := false
	var exit_elapsed := 0.0
	while bool(root.get("Transitioning")) and exit_elapsed < 1.0:
		exit_progress = float(root.get("TransitionProgress"))
		exit_mid_rect = Rect2(map.position, map.size)
		if exit_progress > 0.02 and exit_progress < 0.98 \
				and exit_mid_rect.size.x < strategic_rect.size.x \
				and exit_mid_rect.size.x > desk_rect.size.x:
			observed_exit_mid = true
			break
		await create_timer(0.02).timeout
		exit_elapsed += 0.02
	_assert(observed_exit_mid, "收卷存在可观测的连续中间进度")
	_assert(exit_progress > 0.0 and exit_progress < 1.0, "收卷中间帧进度位于 0..1 之间")
	_assert(exit_mid_rect.size.x < strategic_rect.size.x and exit_mid_rect.size.x > desk_rect.size.x, "收卷中间帧位于全屏与桌面矩形之间")
	_assert(await _wait_for_transition(root, 2.0), "收卷归案转场在时限内完成")
	_assert(not bool(root.get("InputLocked")) and not bool(root.get("Transitioning")), "收卷完成后解除输入锁")
	_assert(desk.visible and not map.visible and not map_layer.visible, "收卷完成后恢复真实御案并隐藏策略层")
	_assert(Rect2(map.position, map.size).is_equal_approx(desk_rect), "收卷最终精确回到桌面舆图矩形")

	_assert(_source_has_no_authoritative_state_dependency(), "MainUi 与只读模型不依赖游戏权威状态模块")
	_finish()

func _wait_for_transition(root: Control, timeout_seconds: float) -> bool:
	var elapsed := 0.0
	while bool(root.get("Transitioning")) and elapsed < timeout_seconds:
		await create_timer(0.02).timeout
		elapsed += 0.02
	return not bool(root.get("Transitioning"))

func _source_has_no_authoritative_state_dependency() -> bool:
	var main_source := FileAccess.get_file_as_string("res://src/Ming.Godot/scripts/MainUi.cs")
	var model_source := FileAccess.get_file_as_string("res://src/Ming.Godot/scripts/ReadModels/MemorialDeskReadModel.cs")
	var combined := main_source + "\n" + model_source
	return not combined.contains("using Ming.Domain") \
		and not combined.contains("using Ming.Simulation") \
		and not combined.contains("WorldState") \
		and not combined.contains("SimulationKernel")

func _assert(condition: bool, message: String) -> void:
	if condition:
		print("PASS: " + message)
	else:
		failures.append(message)

func _on_timeout() -> void:
	if finished:
		return
	failures.append("MainUi focused acceptance 超时")
	_finish()

func _finish() -> void:
	if finished:
		return
	finished = true
	if failures.is_empty():
		print("MAIN_UI_TRANSITION_ACCEPTANCE: PASS")
	else:
		for failure in failures:
			push_error("FAIL: " + failure)
		print("MAIN_UI_TRANSITION_ACCEPTANCE: FAIL (%d)" % failures.size())
	quit(0 if failures.is_empty() else 1)
