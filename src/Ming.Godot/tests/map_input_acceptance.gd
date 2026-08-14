extends SceneTree

const GOOD_MANIFEST := "res://assets/maps/generated/ming_1629/map-manifest.json"
const SMALL_IMAGE := "res://assets/ui/generated/ming_ui_v2/icons/icon-decree.png"
# P1-2：实际显示底图由代码侧权威契约锁定；以下负例直接操纵磁盘字节，
# 校验契约必须在 GD.Load/状态发布前 fail-closed。
const DERIVED_MING_1629 := "res://assets/ui/generated/ming_ui_v2/maps/ming_1629-physical.png"
const FORMAL_PHYSICAL := "res://assets/maps/generated/ming_1629/physical-base.png"

var failures: Array[String] = []


func _init() -> void:
	var scene := load("res://src/Ming.Godot/scenes/ui_preview.tscn") as PackedScene
	_assert(scene != null, "地图验收场景可加载")
	if scene == null:
		_finish()
		return

	var root := scene.instantiate() as Control
	root.name = "MapInputAcceptanceRoot"
	root.set_size(Vector2(1600, 960))
	get_root().add_child(root)
	await process_frame
	await process_frame

	var map := root.get_node_or_null("MapView") as Control
	_assert(map != null, "MapView 节点存在")
	if map != null:
		_assert(map.LoadedFromManifest, "合法清单完整加载")
		_test_cover_clicks(map)
		_test_hidden_points_do_not_win(map)
		_test_semantic_surface(map)
		await _test_actual_label_count(map)
		_test_fail_closed_manifests(map)
		_test_self_consistent_small_canvas(map)
		_test_derived_texture_contract(map)

	root.queue_free()
	await process_frame
	_finish()


func _test_cover_clicks(map: Control) -> void:
	# MainUi 的桌面态把 MapView 放在小卷轴中；本聚焦测试直接给它真实战略视口，
	# 让鼠标坐标必须位于可见控件内，而不是向视口外人工投递事件。
	print("MAP_INPUT_INITIAL_SIZE: %s" % map.size)
	map.set_size(Vector2(1600, 820))
	map.EnterStrategicView()
	map.ResetView()
	for step in range(20):
		_send_wheel(map, MOUSE_BUTTON_WHEEL_UP, Vector2(map.size.x * 0.5, map.size.y * 0.5))
	_assert(is_equal_approx(map.Zoom, 4.0), "点击验收使用近景 LOD")

	for place_id in ["beijing", "tongzhou", "shanhaiguan", "ningyuan"]:
		var point: Vector2 = map.GetViewportPointForPlace(place_id)
		_assert(Rect2(Vector2.ZERO, map.size).has_point(point), "%s 的共享 cover 正算落在视口内" % place_id)
		_send_click(map, point)
		_assert(map.SelectedPlaceId == place_id, "真实 _GuiInput 点击选择正确节点：%s" % place_id)

	var content: Rect2 = map.ContentViewportRect
	_assert(content.position.x <= 0.1 and content.position.y <= 0.1, "共享 cover 变换覆盖视口左上边")
	_assert(content.end.x >= map.size.x - 0.1 and content.end.y >= map.size.y - 0.1, "共享 cover 变换覆盖视口右下边")


func _test_hidden_points_do_not_win(map: Control) -> void:
	map.ResetView()
	_assert(map.VisiblePlaceCount < map.PlaceCount, "最远 LOD 确有隐藏节点")
	map.SelectPlace("shanhaiguan")
	var hidden_tongzhou_point: Vector2 = map.GetViewportPointForPlace("tongzhou")
	_send_click(map, hidden_tongzhou_point)
	_assert(map.SelectedPlaceId != "tongzhou", "未绘制的通州不会抢走点击")


func _test_semantic_surface(map: Control) -> void:
	map.SelectPlace("ningyuan")
	var place_summary: String = str(map.SelectedPlaceSummary)
	for token in ["FACT/OPEN", "review_status=accepted", "evidence_status=accepted_evidence", "coordinate_epoch=modern_anchor", "map_representation=approximate_point", "historical_site_status=open"]:
		_assert(place_summary.contains(token), "选中节点摘要公开语义：%s" % token)

	var route_summary: String = str(map.GetRouteSemanticSummary("route-shanhaiguan-ningyuan"))
	for token in ["INFERENCE", "review_status=accepted", "evidence_status=accepted", "claim_status=reviewed_inference"]:
		_assert(route_summary.contains(token), "路线摘要公开语义：%s" % token)

	var legend: String = str(map.SemanticLegend)
	_assert(legend.contains("approximate_point") and legend.contains("INFERENCE") and legend.contains("FACT"), "运行时图例区分近似点、推断路线与事实路线")
	var route_legend: String = str(map.RouteSemanticLegend)
	_assert(route_legend.contains("claim_status=reviewed_inference") and route_legend.contains("evidence_status=accepted"), "运行时路线图例携带 claim/evidence 状态")


func _test_actual_label_count(map: Control) -> void:
	map.EnterStrategicView()
	map.ResetView()
	for step in range(20):
		_send_wheel(map, MOUSE_BUTTON_WHEEL_UP, Vector2(map.size.x * 0.5, map.size.y * 0.5))
	var original_size: Vector2 = map.size
	var wide_count: int = map.VisibleLabelCount
	print("MAP_INPUT_WIDE_LABEL_COUNT: %d/%d" % [wide_count, map.VisiblePlaceCount])
	_assert(wide_count > 0 and wide_count <= map.VisiblePlaceCount, "宽视口标签计数不超过实际可见节点")

	map.set_size(Vector2(360, 220))
	var constrained_count: int = map.VisibleLabelCount
	map.queue_redraw()
	await process_frame
	_assert(map.VisibleLabelCount == constrained_count, "VisibleLabelCount 与实际绘制使用同一裁剪/避让布局")
	_assert(constrained_count < map.VisiblePlaceCount, "窄视口中的裁剪或碰撞过滤会减少实际标签数")
	map.set_size(original_size)


func _test_fail_closed_manifests(map: Control) -> void:
	var source_json := FileAccess.get_file_as_string(GOOD_MANIFEST)
	_assert(not source_json.is_empty(), "合法清单文本可读取")

	for missing_hash in ["physical-base.png", "history-overlay.png"]:
		var missing_hash_manifest := _remove_json_line_containing(source_json, '"%s"' % missing_hash)
		_load_invalid_manifest(map, missing_hash_manifest, "missing-" + missing_hash)
		_assert_fail_closed(map, "缺少必需哈希：%s" % missing_hash)

	var mismatched_hash_manifest := _replace_hash_entry(source_json, "physical-base.png", "physical-base.png", "0".repeat(64))
	_load_invalid_manifest(map, mismatched_hash_manifest, "mismatched-hash")
	_assert_fail_closed(map, "必需底图 SHA-256 不匹配")

	var wrong_size_manifest := source_json.replace(
		"res://assets/maps/generated/ming_1629/physical-base.png",
		SMALL_IMAGE)
	wrong_size_manifest = _replace_hash_entry(
		wrong_size_manifest,
		"physical-base.png",
		SMALL_IMAGE.get_file(),
		FileAccess.get_sha256(SMALL_IMAGE))
	_load_invalid_manifest(map, wrong_size_manifest, "wrong-raster-size")
	_assert_fail_closed(map, "416x756 图片不能替代 2400x1600 地图")

	var invalid_content_manifest := source_json.replace("1814.626197,", "2401.0,")
	_load_invalid_manifest(map, invalid_content_manifest, "invalid-content-rect")
	_assert_fail_closed(map, "content_rect 超出画布时拒绝加载")

	map.LoadManifest(GOOD_MANIFEST)
	_assert(map.LoadedFromManifest and map.PlaceCount > 0 and map.RouteCount > 0, "坏清单后仍可重新完整加载合法清单")


func _remove_json_line_containing(source_json: String, needle: String) -> String:
	var kept: PackedStringArray = []
	for line in source_json.split("\n"):
		if not line.contains(needle):
			kept.append(line)
	return "\n".join(kept)


func _replace_hash_entry(source_json: String, old_file_name: String, new_file_name: String, new_hash: String) -> String:
	var regex := RegEx.new()
	var compile_error := regex.compile('"%s"\\s*:\\s*"[0-9a-fA-F]{64}"' % old_file_name.replace(".", "\\."))
	_assert(compile_error == OK, "哈希夹具正则可编译")
	return regex.sub(source_json, '"%s": "%s"' % [new_file_name, new_hash], true)


func _load_invalid_manifest(map: Control, manifest_json: String, suffix: String) -> void:
	var user_path := "user://map-input-acceptance-%s.json" % suffix.validate_filename()
	var file := FileAccess.open(user_path, FileAccess.WRITE)
	_assert(file != null, "可写入隔离的坏清单夹具：%s" % suffix)
	if file == null:
		return
	file.store_string(manifest_json)
	file = null
	map.LoadManifest(user_path)
	DirAccess.remove_absolute(ProjectSettings.globalize_path(user_path))


func _assert_fail_closed(map: Control, scenario: String) -> void:
	_assert(not map.LoadedFromManifest, "%s：地图保持关闭" % scenario)
	_assert(map.PlaceCount == 0 and map.RouteCount == 0, "%s：没有发布部分 manifest 状态" % scenario)
	_assert(str(map.LoadError) != "" and str(map.SelectedPlaceSummary).begins_with("OPEN"), "%s：只显示中性错误语义" % scenario)


func _test_self_consistent_small_canvas(map: Control) -> void:
	# P1-1：完整自洽错误尺寸负例。manifest 的画布、两张纹理、content_rect、
	# 节点/路线坐标全部按 416x756 自洽，唯一"错误"是画布不是权威 2400x1600。
	# 这种清单也必须 fail-closed 且不发布任何部分状态。
	var small_sha := FileAccess.get_sha256(SMALL_IMAGE)
	_assert(small_sha.length() == 64, "416x756 夹具可取得真实纹理哈希")
	var small_manifest := {
		"schema_version": 1,
		"scenario_id": "test-416x756-self-consistent",
		"snapshot_date": "1629-01-01",
		"canvas": {
			"width": 416,
			"height": 756,
			"padding": 10,
			"content_rect": [10.0, 10.0, 396.0, 736.0]
		},
		"assets": {
			"physical_base": SMALL_IMAGE,
			"history_overlay": SMALL_IMAGE
		},
		"asset_sha256": {
			"icon-decree.png": small_sha
		},
		"historical_content": {
			"snapshot_date": "1629-01-01",
			"warning": "自洽小画布负例",
			"claim_status": "reviewed_p0_evidence",
			"geometry_role": "presentation_only_not_simulation_topology"
		},
		"research_baseline": {
			"geometry_depict_date": "1391-01-01",
			"historical_fit_status": "research_baseline_only",
			"visible": false
		},
		"places": [
			_small_place("beijing", 200.0, 300.0),
			_small_place("ningyuan", 300.0, 500.0)
		],
		"routes": [
			{
				"id": "route-1",
				"from_place_id": "beijing",
				"to_place_id": "ningyuan",
				"review_status": "accepted",
				"evidence_status": "accepted",
				"claim_status": "reviewed_inference",
				"points": [
					{"map_x": 200.0, "map_y": 300.0},
					{"map_x": 300.0, "map_y": 500.0}
				]
			}
		]
	}
	_load_invalid_manifest(map, JSON.stringify(small_manifest), "self-consistent-416x756")
	_assert_fail_closed(map, "416x756 全自洽错误画布")
	_assert(str(map.LoadError).contains("2400"), "416x756 画布负例的错误信息指明权威画布")


func _small_place(id: String, map_x: float, map_y: float) -> Dictionary:
	return {
		"id": id,
		"kind": "city",
		"name_zh": id,
		"review_status": "accepted",
		"evidence_status": "accepted_anchor",
		"historical_site_status": "open",
		"coordinate_epoch": "modern_anchor",
		"map_representation": "approximate_point",
		"map_x": map_x,
		"map_y": map_y
	}


func _test_derived_texture_contract(map: Control) -> void:
	# P1-2：界面显示底图（UI 纸色派生图）的路径 + SHA-256 + 2400x1600
	# 是代码侧权威契约。三种攻击都必须 fail-closed；每次操作后立即恢复字节，
	# 并验证能重新完整加载，避免把工作树留在被篡改状态。
	var derived_original := FileAccess.get_file_as_bytes(DERIVED_MING_1629)
	_assert(derived_original.size() > 0, "派生底图原字节可备份")

	# 1) 同尺寸篡改：2400x1600 纯紫图，尺寸相同但字节不同 → SHA-256 契约拒绝
	var purple := Image.create(2400, 1600, false, Image.FORMAT_RGB8)
	purple.fill(Color(0.5, 0.0, 0.6))
	var save_error := purple.save_png(DERIVED_MING_1629)
	_assert(save_error == OK, "可写入同尺寸篡改夹具")
	map.LoadManifest(GOOD_MANIFEST)
	_assert_fail_closed(map, "派生显示底图同尺寸篡改")
	_assert(str(map.LoadError).contains("SHA-256"), "同尺寸篡改命中 SHA-256 契约")
	_restore_bytes(DERIVED_MING_1629, derived_original)
	map.LoadManifest(GOOD_MANIFEST)
	_assert(map.LoadedFromManifest, "恢复派生底图字节后重新加载成功")

	# 2) 缺文件：移除派生底图文件 → 契约文件检查拒绝
	var moved := ProjectSettings.globalize_path(DERIVED_MING_1629) + ".moved-away"
	var rename_error := DirAccess.rename_absolute(
		ProjectSettings.globalize_path(DERIVED_MING_1629), moved)
	_assert(rename_error == OK, "可移走派生底图文件")
	map.LoadManifest(GOOD_MANIFEST)
	_assert_fail_closed(map, "派生显示底图缺失")
	_assert(str(map.LoadError).contains("文件不可用"), "缺文件命中契约文件检查")
	rename_error = DirAccess.rename_absolute(moved, ProjectSettings.globalize_path(DERIVED_MING_1629))
	_assert(rename_error == OK, "可恢复派生底图文件")
	map.LoadManifest(GOOD_MANIFEST)
	_assert(map.LoadedFromManifest, "恢复派生底图文件后重新加载成功")

	# 3) 错 hash：manifest 声明的正式底图哈希与磁盘字节不符
	# （同尺寸不同内容，画布与 manifest 其余部分不变）→ manifest 级哈希校验拒绝
	var formal_original := FileAccess.get_file_as_bytes(FORMAL_PHYSICAL)
	_assert(formal_original.size() > 0, "正式物理底图原字节可备份")
	var swapped := Image.create(2400, 1600, false, Image.FORMAT_RGB8)
	swapped.fill(Color(0.62, 0.18, 0.1))
	save_error = swapped.save_png(FORMAL_PHYSICAL)
	_assert(save_error == OK, "可写入错哈希夹具")
	map.LoadManifest(GOOD_MANIFEST)
	_assert_fail_closed(map, "manifest 声明哈希与磁盘字节不符")
	_assert(str(map.LoadError).contains("SHA-256"), "错哈希命中 manifest 哈希校验")
	_restore_bytes(FORMAL_PHYSICAL, formal_original)
	map.LoadManifest(GOOD_MANIFEST)
	_assert(map.LoadedFromManifest, "恢复正式底图字节后重新加载成功")


func _restore_bytes(path: String, bytes: PackedByteArray) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	_assert(file != null, "可打开恢复目标：%s" % path)
	if file == null:
		return
	file.store_buffer(bytes)
	file = null


func _send_click(map: Control, position: Vector2) -> void:
	_send_mouse(map, MOUSE_BUTTON_LEFT, position, true)
	_send_mouse(map, MOUSE_BUTTON_LEFT, position, false)


func _send_mouse(map: Control, button: int, position: Vector2, pressed: bool) -> void:
	var event := InputEventMouseButton.new()
	event.button_index = button
	event.position = position
	event.pressed = pressed
	map._gui_input(event)


func _send_wheel(map: Control, button: int, position: Vector2) -> void:
	_send_mouse(map, button, position, true)


func _assert(condition: bool, message: String) -> void:
	if condition:
		print("PASS: " + message)
	else:
		failures.append(message)


func _finish() -> void:
	if failures.is_empty():
		print("MAP_INPUT_ACCEPTANCE: PASS")
	else:
		for failure in failures:
			push_error("FAIL: " + failure)
		print("MAP_INPUT_ACCEPTANCE: FAIL (%d)" % failures.size())
	quit(0 if failures.is_empty() else 1)
