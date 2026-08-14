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
	var decree_entry := root.find_child("OpenDecreePanel", true, false) as BaseButton
	_assert(decree_entry != null, "政令入口按钮存在")
	var endgame_entry := root.find_child("OpenEndgameReport", true, false) as BaseButton
	_assert(endgame_entry != null, "终局复盘入口按钮存在")
	if guide_entry == null or guide_panel == null:
		_finish()
		return

	await _test_dispatch_action_must_succeed_in_real_ningyuan_scenario(root)

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

	if decree_entry != null:
		guide_entry.emit_signal("pressed")
		await process_frame
		_assert(bool(guide_panel.visible), "再次打开引导后可再次进入")
		decree_entry.emit_signal("pressed")
		await process_frame
		_assert(not bool(guide_panel.visible), "打开政令后应关闭引导面板")
	if endgame_entry != null:
		guide_entry.emit_signal("pressed")
		await process_frame
		_assert(bool(guide_panel.visible), "再次打开引导后可再次进入")
		endgame_entry.emit_signal("pressed")
		await process_frame
		_assert(not bool(guide_panel.visible), "打开终局复盘后应关闭引导面板")

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


# 审计 P1-UI-01 端到端：引导第 2 步核心动作"调粮五千石"必须在真实 1629 场景中可执行——
# 按钮通过权威路线投影选择当前可行路线（不再硬编码山海关），命令被内核受理并产生在途粮队。
func _test_dispatch_action_must_succeed_in_real_ningyuan_scenario(root: Control) -> void:
	var dispatch := _find_by_name(root, "RealtimeDispatchGrain", "Button") as BaseButton
	var outcome := _find_by_name(root, "RealtimeOutcome", "Label") as Label
	var stockpiles := _find_by_name(root, "RealtimeStockpiles", "Label") as Label
	_assert(dispatch != null, "调粮按钮 RealtimeDispatchGrain 存在")
	_assert(outcome != null and stockpiles != null, "顶栏结果与库存标签存在")
	if dispatch == null or outcome == null or stockpiles == null:
		return
	dispatch.emit_signal("pressed")
	# 等内核在帧推进中处理收件箱（RealtimeWorldBridge 每帧 Advance）。
	for _i in range(30):
		await process_frame
	var outcome_text := str(outcome.text)
	var stockpiles_text := str(stockpiles.text)
	_assert(outcome_text.contains("受理"), "调粮命令必须被内核受理：%s" % outcome_text)
	_assert(not outcome_text.contains("拒绝"), "调粮命令不得被拒绝：%s" % outcome_text)
	_assert(stockpiles_text.contains("在途 1 批"), "受理后必须产生在途粮队：%s" % stockpiles_text)
	_assert(stockpiles_text.contains("beijing:5000石"), "调粮必须真实扣除北京来源仓 5000 石：%s" % stockpiles_text)


func _find_by_name(root: Node, node_name: String, node_class: String) -> Node:
	for child in root.find_children("*", node_class, true, false):
		if str(child.name) == node_name:
			return child
	return null


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
