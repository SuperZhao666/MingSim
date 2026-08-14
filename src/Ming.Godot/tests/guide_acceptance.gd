extends SceneTree

const SCENE_PATH := "res://src/Ming.Godot/scenes/ui_preview.tscn"
const TEST_TIMEOUT_SECONDS := 16.0

var failures: Array[String] = []
var finished := false


func _init() -> void:
	var watchdog := create_timer(TEST_TIMEOUT_SECONDS)
	watchdog.timeout.connect(_on_timeout)

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

	root.name = "GuideAcceptanceRoot"
	root.set_size(Vector2(1600, 960))
	get_root().add_child(root)
	await process_frame
	await process_frame

	var guide_entry := root.find_child("OpenGuidePanel", true, false) as BaseButton
	_assert(guide_entry != null, "新手引导入口按钮存在")
	var guide_panel := root.find_child("GuidePanel", true, false) as Control
	_assert(guide_panel != null, "引导面板节点存在")
	if guide_entry == null or guide_panel == null:
		_finish()
		return

	guide_entry.emit_signal("pressed")
	await process_frame
	_assert(bool(guide_panel.visible), "点击入口后引导面板显示")
	var step_body := guide_panel.find_child("GuideStepBody", true, false) as Label
	var next_button := guide_panel.find_child("GuideNext", true, false) as BaseButton
	var skip_button := guide_panel.find_child("GuideSkip", true, false) as BaseButton
	_assert(step_body != null and step_body.text.strip_edges() != "", "引导首步文案非空")
	_assert(next_button != null, "引导面板有下一步/完成按钮")
	_assert(skip_button != null, "引导面板有跳过按钮")

	if next_button != null:
		next_button.emit_signal("pressed")
		await process_frame
		_assert(bool(guide_panel.visible), "第二步后面板仍保持显示")
		next_button.emit_signal("pressed")
		await process_frame
		next_button.emit_signal("pressed")
		await process_frame
		next_button.emit_signal("pressed")
		await process_frame
		_assert(not bool(guide_panel.visible), "完成步骤后引导面板关闭")

	guide_entry.emit_signal("pressed")
	await process_frame
	_assert(bool(guide_panel.visible), "再次点击入口后引导面板可重新打开")
	if skip_button != null:
		skip_button.emit_signal("pressed")
		await process_frame
		_assert(not bool(guide_panel.visible), "跳过按钮可关闭引导面板")

	# 验收脚本源代码层面不允许出现可写核心状态入口与推进函数。
	var guide_source := FileAccess.get_file_as_string("res://src/Ming.Godot/scripts/GuidePanel.cs")
	var forbidden := ["WorldState.", "SimulationKernel", "AdvanceTo(", "AdvanceTo(", "推进时间"]
	_assert(not _contains_forbidden_tokens(guide_source, forbidden), "guide_acceptance 目标脚本未引用可写内核/时间推进 API")

	_finish()


func _contains_forbidden_tokens(source: String, forbidden: Array) -> bool:
	for token in forbidden:
		if source.find(token) >= 0:
			push_error("禁用 token 命中: " + token)
			return true
	return false


func _assert(condition: bool, message: String) -> void:
	if condition:
		print("PASS: " + message)
	else:
		failures.append(message)


func _on_timeout() -> void:
	if finished:
		return
	failures.append("M3 guide acceptance 超时")
	_finish()


func _finish() -> void:
	if finished:
		return
	finished = true
	if failures.is_empty():
		print("M3_GUIDE_ACCEPTANCE: PASS")
	else:
		for failure in failures:
			push_error("FAIL: " + failure)
		print("M3_GUIDE_ACCEPTANCE: FAIL (%d)" % failures.size())
	quit(0 if failures.is_empty() else 1)
