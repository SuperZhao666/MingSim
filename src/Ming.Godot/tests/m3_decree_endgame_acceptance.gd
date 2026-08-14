extends SceneTree
# M3 政令与终局复盘面板 headless 验收（测试先行：先失败，再实现到通过）。
# 覆盖：
# 1) 政令入口按钮存在；打开后模板下拉来自 world.json decrees[]（三种模板）；
# 2) 提交默认草拟政令后，只读 ReadModel 的政令列表出现该政令并显示状态用语；
# 3) 终局复盘入口存在；打开后正文含 EvaluateEndgame 六维文案子串。

const SCENE_PATH := "res://src/Ming.Godot/scenes/ui_preview.tscn"
const EXPECTED_SCRIPT_PATH := "res://src/Ming.Godot/scripts/MainUi.cs"
const TEST_TIMEOUT_SECONDS := 25.0

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
	root.name = "M3DecreeEndgameAcceptanceRoot"
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

	# --- 政令面板：入口按钮 + 模板 + 提交 + 列表状态 ---
	var open_decree := root.find_child("OpenDecreePanel", true, false) as BaseButton
	_assert(open_decree != null, "政令面板入口按钮存在")
	var decree_panel := root.find_child("DecreePanel", true, false) as Control
	_assert(decree_panel != null, "政令面板节点存在")
	if open_decree != null and decree_panel != null:
		open_decree.emit_signal("pressed")
		await process_frame
		_assert(decree_panel.visible, "点击政令按钮后政令面板显示")
		var template_select := decree_panel.find_child("DecreeTemplateSelect", true, false) as OptionButton
		_assert(template_select != null and template_select.item_count == 3,
			"政令模板下拉来自 world.json decrees[] 三种模板")
		_assert(int(decree_panel.get("DecreeCount")) == 0, "提交前政令列表为空")
		var submit := decree_panel.find_child("SubmitDecree", true, false) as BaseButton
		_assert(submit != null, "政令提交按钮存在")
		if submit != null:
			submit.emit_signal("pressed")
			var waited := 0.0
			while int(decree_panel.get("DecreeCount")) < 1 and waited < 6.0:
				await create_timer(0.1).timeout
				waited += 0.1
			_assert(int(decree_panel.get("DecreeCount")) == 1, "提交后政令列表（只读 ReadModel）出现该政令")
			var rows := decree_panel.find_children("DecreeRow-*", "Label", true, false)
			_assert(rows.size() >= 1, "政令列表渲染出政令行")
			if not rows.is_empty():
				var first_text := (rows[0] as Label).text
				_assert("催饷令" in first_text, "政令行文案含模板名（%s）" % first_text)
				_assert("执行中" in first_text, "政令行显示状态用语（%s）" % first_text)

	# --- 终局复盘面板：入口按钮 + 六维文案 ---
	var open_endgame := root.find_child("OpenEndgameReport", true, false) as BaseButton
	_assert(open_endgame != null, "终局复盘入口按钮存在")
	var endgame_panel := root.find_child("EndgameReportPanel", true, false) as Control
	_assert(endgame_panel != null, "终局复盘面板节点存在")
	if open_endgame != null and endgame_panel != null:
		open_endgame.emit_signal("pressed")
		await process_frame
		_assert(endgame_panel.visible, "点击终局复盘按钮后面板显示")
		var body := endgame_panel.find_child("EndgameReportBody", true, false) as Label
		_assert(body != null and body.text != "", "终局复盘正文非空")
		if body != null:
			for dim in ["宁远可用粮", "前线战备", "中央财政", "地方负担", "大臣信任", "执行与审计"]:
				_assert(dim in body.text, "终局复盘文案含六维：%s" % dim)

	if failures.is_empty():
		print("M3_DECREE_ENDGAME_ACCEPTANCE: PASS")
	else:
		for failure in failures:
			push_error("FAIL: " + failure)
		print("M3_DECREE_ENDGAME_ACCEPTANCE: FAIL (%d)" % failures.size())
	_finish()


func _assert(condition: bool, message: String) -> void:
	if condition:
		print("PASS: " + message)
	else:
		failures.append(message)


func _on_timeout() -> void:
	if finished:
		return
	failures.append("M3 政令/终局验收超时")
	_finish()


func _finish() -> void:
	if finished:
		return
	finished = true
	quit(0 if failures.is_empty() else 1)
