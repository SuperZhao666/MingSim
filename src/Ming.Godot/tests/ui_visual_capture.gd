extends SceneTree

const SCENE_PATH := "res://src/Ming.Godot/scenes/ui_preview.tscn"
const EXPECTED_MAIN_UI_SCRIPT := "res://src/Ming.Godot/scripts/MainUi.cs"
const OVERVIEW_MANIFEST := "res://assets/maps/generated/ming_1629/map-manifest.json"
const LIAOXI_MANIFEST := "res://assets/maps/generated/ming_1629_liaoxi/map-manifest.json"
const SUPPORTED_STATES := [
	"desk-overview",
	"memorial-open",
	"map-strategic",
	"map-near-labels",
	"map-west-clamp",
	"disabled",
	"seal-pressed",
]

# These semantic names are the visual-test contract for the upcoming desk UI.
# Compatibility names/text keep the current prototype capturable without relying
# on its old left/right panel hierarchy.
const DESK_NAMES := ["DeskOverview", "DeskLayer", "ImperialDesk", "MainUi"]
const MAP_NAMES := ["StrategicMap", "MapView"]
const MEMORIAL_SURFACE_NAMES := [
	"MemorialOpen",
	"MemorialOpenPanel",
	"MemorialViewer",
	"MemorialSheet",
]
const MEMORIAL_TRIGGER_NAMES := [
	"OpenMemorialButton",
	"PrimaryMemorial",
	"DeskMemorial-1",
	"Memorial-辽西急报",
]
const STRATEGIC_LAYER_NAMES := ["MapStrategic", "StrategicMapLayer", "MapLayer"]
const DRAFT_BUTTON_NAMES := ["OpenEdictDraftButton", "OpenDecreeDraft", "DraftEdictButton", "DraftButton"]
const PREVIEW_BUTTON_NAMES := ["OpenConfirmButton", "PreviewEdictButton", "PreviewButton"]
const CONFIRM_PANEL_NAMES := ["EdictConfirmPanel", "MemorialConfirmPanel", "ConfirmPanel"]
const SEAL_BUTTON_NAMES := ["SealButton", "ConfirmSealButton", "SubmitIntentButton"]

var focus_liaoxi := false
var output_path := ""
var capture_state := "desk-overview"
var capture_size := Vector2i(1600, 960)
var failure_message := ""
var finished := false


func _init() -> void:
	create_timer(20.0).timeout.connect(_on_timeout)
	call_deferred("_capture")


func _capture() -> void:
	if not _parse_arguments():
		_finish_failure(2)
		return

	var scene := load(SCENE_PATH) as PackedScene
	if scene == null:
		_record_failure("UI_VISUAL_CAPTURE_SCENE_LOAD_FAILED: %s" % SCENE_PATH)
		_finish_failure(1)
		return

	var instance := scene.instantiate()
	if not instance is Control:
		_record_failure("UI_VISUAL_CAPTURE_ROOT_TYPE_ERROR: expected Control, got %s" % instance.get_class())
		instance.queue_free()
		_finish_failure(1)
		return

	var root := instance as Control
	get_root().size = capture_size
	root.set_size(Vector2(capture_size))
	get_root().add_child(root)
	await process_frame
	await process_frame
	var attached_script: Script = root.get_script()
	if attached_script == null \
		or attached_script.resource_path != EXPECTED_MAIN_UI_SCRIPT \
		or attached_script.get_class() != "CSharpScript" \
		or not root.has_method("SetStrategicView"):
		_record_failure("UI_VISUAL_CAPTURE_CSHARP_UI_MISSING: MainUi C# script or methods are unavailable")
		_finish_failure(1)
		return
	var map := root.get_node_or_null("MapView")
	if map == null or not map.has_method("LoadManifest") or not bool(map.get("LoadedFromManifest")):
		_record_failure("UI_VISUAL_CAPTURE_MAP_TYPE_ERROR: real C# MapView did not load its manifest")
		_finish_failure(1)
		return
	var background := root.find_child("StudyBackground", true, false) as TextureRect
	var desk := root.find_child("DeskLayer", true, false) as Control
	if background == null or background.texture == null or desk == null or desk.get_child_count() < 8:
		_record_failure("UI_VISUAL_CAPTURE_EMPTY_SHELL: required non-empty C# UI children/textures are missing")
		_finish_failure(1)
		return

	if focus_liaoxi and not _load_liaoxi_focus(root):
		_finish_failure(3)
		return
	if not await _apply_capture_state(root):
		_finish_failure(3)
		return

	await process_frame
	await process_frame
	await process_frame
	if output_path != "" and not _save_capture():
		_finish_failure(4)
		return

	print("UI_VISUAL_CAPTURE_READY: state=%s size=%dx%d focus=%s" % [
		capture_state,
		capture_size.x,
		capture_size.y,
		"liaoxi" if focus_liaoxi else "overview",
	])
	finished = true
	quit(0)


func _parse_arguments() -> bool:
	for argument in OS.get_cmdline_user_args():
		if argument == "--focus-liaoxi":
			focus_liaoxi = true
		elif argument.begins_with("--output="):
			output_path = argument.trim_prefix("--output=")
		elif argument.begins_with("--state="):
			capture_state = argument.trim_prefix("--state=")
		elif argument.begins_with("--size="):
			if not _parse_capture_size(argument.trim_prefix("--size=")):
				return false
		elif argument.begins_with("--width="):
			if not _parse_capture_size(argument.trim_prefix("--width=")):
				return false

	if capture_state not in SUPPORTED_STATES:
		_record_failure("UI_VISUAL_CAPTURE_UNKNOWN_STATE: '%s'; supported=%s" % [capture_state, SUPPORTED_STATES])
		return false
	if output_path.strip_edges() == "" and output_path != "":
		_record_failure("UI_VISUAL_CAPTURE_INVALID_OUTPUT: --output must not be blank")
		return false
	return true


func _parse_capture_size(value: String) -> bool:
	var normalized := value.to_lower().strip_edges()
	if normalized in ["1280", "1280x768"]:
		capture_size = Vector2i(1280, 768)
		return true
	if normalized == "1280x960":
		capture_size = Vector2i(1280, 960)
		return true
	if normalized in ["1600", "1600x960"]:
		capture_size = Vector2i(1600, 960)
		return true
	_record_failure("UI_VISUAL_CAPTURE_INVALID_SIZE: '%s'; expected 1280, 1280x768, 1280x960, 1600, or 1600x960" % value)
	return false


func _apply_capture_state(root: Control) -> bool:
	match capture_state:
		"desk-overview":
			return _prepare_desk_overview(root)
		"memorial-open":
			return await _prepare_memorial_open(root)
		"map-strategic":
			return await _prepare_map_strategic(root)
		"map-near-labels":
			return await _prepare_map_near_labels(root)
		"map-west-clamp":
			return await _prepare_map_west_clamp(root)
		"disabled":
			return _prepare_seal_state(root, true, false)
		"seal-pressed":
			return _prepare_seal_state(root, false, true)
	_record_failure("UI_VISUAL_CAPTURE_UNKNOWN_STATE: '%s'" % capture_state)
	return false


func _prepare_desk_overview(root: Control) -> bool:
	var desk := _find_node_by_names(root, DESK_NAMES)
	if desk == null:
		return _missing_node("desk overview", DESK_NAMES)
	if root.has_method("SetStrategicView"):
		root.call("SetStrategicView", false)
	var strategic_layer := _find_node_by_names(root, STRATEGIC_LAYER_NAMES)
	if strategic_layer != null:
		_set_visible_if_canvas_item(strategic_layer, false)
	var map := _find_node_by_names(root, MAP_NAMES)
	if map != null:
		_set_visible_if_canvas_item(map, false)
	_set_visible_if_canvas_item(desk, true)
	if desk is CanvasItem and not (desk as CanvasItem).is_visible_in_tree():
		_record_failure("UI_VISUAL_CAPTURE_STATE_NOT_REACHED: state=desk-overview desk=%s is not visible" % desk.name)
		return false
	if map is CanvasItem and (map as CanvasItem).is_visible_in_tree():
		_record_failure("UI_VISUAL_CAPTURE_STATE_NOT_REACHED: state=desk-overview map=%s is still visible" % map.name)
		return false
	return true


func _prepare_memorial_open(root: Control) -> bool:
	var surface := _find_node_by_names(root, MEMORIAL_SURFACE_NAMES)
	var trigger := _find_button(root, MEMORIAL_TRIGGER_NAMES, [])
	if trigger == null and surface == null:
		return _missing_node("memorial surface or trigger", MEMORIAL_SURFACE_NAMES + MEMORIAL_TRIGGER_NAMES)
	if trigger != null:
		if trigger.disabled:
			_record_failure("UI_VISUAL_CAPTURE_NODE_DISABLED: state=memorial-open node=%s" % trigger.name)
			return false
		trigger.emit_signal("pressed")
		await process_frame
		surface = _find_node_by_names(root, MEMORIAL_SURFACE_NAMES)
	if surface == null:
		_record_failure("UI_VISUAL_CAPTURE_STATE_NOT_REACHED: state=memorial-open trigger=%s did not create a named surface; expected=%s" % [
			trigger.name,
			MEMORIAL_SURFACE_NAMES,
		])
		return false
	_set_visible_if_canvas_item(surface, true)
	if surface is CanvasItem and not (surface as CanvasItem).is_visible_in_tree():
		_record_failure("UI_VISUAL_CAPTURE_STATE_NOT_REACHED: state=memorial-open surface=%s is not visible in tree" % surface.name)
		return false
	return true


func _prepare_map_strategic(root: Control) -> bool:
	var map := _require_map(root)
	if map == null:
		return false
	if not await _enter_strategic_map(root, map):
		return false
	if not _call_required(map, "LoadManifest", "strategic overview manifest", [OVERVIEW_MANIFEST]):
		return false
	if not _call_required(map, "ResetView", "strategic map reset"):
		return false
	if not _call_required(map, "SelectPlace", "strategic map selection", ["beijing"]):
		return false
	return true


func _prepare_map_near_labels(root: Control) -> bool:
	var map := _require_map(root)
	if map == null:
		return false
	if not await _enter_strategic_map(root, map):
		return false
	if not _call_required(map, "ResetView", "near-label map reset"):
		return false
	var center := Vector2(map.size.x * 0.5, map.size.y * 0.5)
	for _step in range(15):
		_send_wheel(map, MOUSE_BUTTON_WHEEL_UP, center)
	if not _call_required(map, "SelectPlace", "near-label map selection", ["ningyuan"]):
		return false
	return true


func _prepare_map_west_clamp(root: Control) -> bool:
	var map := _require_map(root)
	if map == null:
		return false
	if not await _enter_strategic_map(root, map):
		return false
	if not _call_required(map, "ResetView", "west-clamp map reset"):
		return false
	var center := Vector2(map.size.x * 0.5, map.size.y * 0.5)
	for _step in range(7):
		_send_wheel(map, MOUSE_BUTTON_WHEEL_UP, center)
	_send_drag(map, center, Vector2(10000, 0))
	return true


func _prepare_seal_state(root: Control, disabled: bool, pressed: bool) -> bool:
	var seal := _find_button(root, SEAL_BUTTON_NAMES, ["提交待核验 Intent"])
	if seal == null:
		if not _open_confirm_surface(root):
			return false
		seal = _find_button(root, SEAL_BUTTON_NAMES, ["提交待核验 Intent"])
		if seal == null:
			return _missing_node("seal button", SEAL_BUTTON_NAMES, ["提交待核验 Intent"])
	_show_control_ancestors(seal, root)
	seal.disabled = disabled
	if pressed:
		seal.toggle_mode = true
	seal.set_pressed_no_signal(pressed)
	return true


func _open_confirm_surface(root: Control) -> bool:
	var confirm_panel := _find_node_by_names(root, CONFIRM_PANEL_NAMES)
	if confirm_panel != null:
		_set_visible_if_canvas_item(confirm_panel, true)
		return true

	# Compatibility path for the current prototype. It searches controls globally
	# by semantic Name first and never assumes a left/right panel location.
	var draft := _find_button(root, DRAFT_BUTTON_NAMES, ["拟定结构化政令"])
	if draft == null:
		return _missing_node("open-draft button", DRAFT_BUTTON_NAMES, ["拟定结构化政令"])
	if draft.disabled:
		_record_failure("UI_VISUAL_CAPTURE_NODE_DISABLED: role=open-draft button node=%s" % draft.name)
		return false
	draft.emit_signal("pressed")

	var preview := _find_button(root, PREVIEW_BUTTON_NAMES, ["查看确认"])
	if preview == null:
		return _missing_node("open-confirm button", PREVIEW_BUTTON_NAMES, ["查看确认"])
	if preview.disabled:
		_record_failure("UI_VISUAL_CAPTURE_NODE_DISABLED: role=open-confirm button node=%s" % preview.name)
		return false
	preview.emit_signal("pressed")
	return true


func _load_liaoxi_focus(root: Control) -> bool:
	var map := _require_map(root)
	if map == null:
		return false
	if not _call_required(map, "LoadManifest", "liaoxi manifest", [LIAOXI_MANIFEST]):
		return false
	return _call_required(map, "SelectPlace", "liaoxi selection", ["ningyuan"])


func _require_map(root: Control) -> Control:
	var node := _find_node_by_names(root, MAP_NAMES)
	if node == null:
		_missing_node("strategic map", MAP_NAMES)
		return null
	if not node is Control:
		_record_failure("UI_VISUAL_CAPTURE_NODE_TYPE_ERROR: role=strategic map node=%s expected=Control actual=%s" % [node.name, node.get_class()])
		return null
	return node as Control


func _find_node_by_names(root: Node, expected_names: Array) -> Node:
	for expected_name in expected_names:
		if root.name == StringName(expected_name):
			return root
		var candidate := root.find_child(expected_name, true, false)
		if candidate != null:
			return candidate
	return null


func _find_button(root: Node, expected_names: Array, fallback_texts: Array) -> BaseButton:
	var named := _find_node_by_names(root, expected_names)
	if named != null:
		if named is BaseButton:
			return named as BaseButton
		_record_failure("UI_VISUAL_CAPTURE_NODE_TYPE_ERROR: node=%s expected=BaseButton actual=%s" % [named.name, named.get_class()])
		return null
	for child in root.find_children("*", "Button", true, false):
		if child is Button and (child as Button).text in fallback_texts:
			return child as BaseButton
	return null


func _enter_strategic_map(root: Control, map: Control) -> bool:
	var layer := _find_node_by_names(root, STRATEGIC_LAYER_NAMES)
	if layer == null:
		return _missing_node("strategic map layer", STRATEGIC_LAYER_NAMES)
	if root.has_method("SetStrategicView"):
		root.call("SetStrategicView", true)
		var elapsed := 0.0
		while bool(root.get("Transitioning")) and elapsed < 2.0:
			await create_timer(0.02).timeout
			elapsed += 0.02
		if bool(root.get("Transitioning")):
			_record_failure("UI_VISUAL_CAPTURE_TRANSITION_TIMEOUT: strategic map did not settle")
			return false
	else:
		_set_visible_if_canvas_item(layer, true)
		_set_visible_if_canvas_item(map, true)
	if layer is CanvasItem and not (layer as CanvasItem).is_visible_in_tree():
		_record_failure("UI_VISUAL_CAPTURE_STATE_NOT_REACHED: state=%s strategic layer=%s is not visible" % [capture_state, layer.name])
		return false
	if not map.is_visible_in_tree():
		_record_failure("UI_VISUAL_CAPTURE_STATE_NOT_REACHED: state=%s map=%s is not visible" % [capture_state, map.name])
		return false
	return true


func _call_required(node: Node, method_name: StringName, role: String, arguments: Array = []) -> bool:
	if not node.has_method(method_name):
		_record_failure("UI_VISUAL_CAPTURE_MISSING_METHOD: role=%s node=%s method=%s" % [role, node.name, method_name])
		return false
	node.callv(method_name, arguments)
	return true


func _send_wheel(map: Control, button: MouseButton, position: Vector2) -> void:
	var event := InputEventMouseButton.new()
	event.button_index = button
	event.position = position
	event.pressed = true
	map._gui_input(event)


func _send_drag(map: Control, start: Vector2, delta: Vector2) -> void:
	var down := InputEventMouseButton.new()
	down.button_index = MOUSE_BUTTON_LEFT
	down.position = start
	down.pressed = true
	map._gui_input(down)
	var motion := InputEventMouseMotion.new()
	motion.position = start + delta
	motion.relative = delta
	map._gui_input(motion)
	var up := InputEventMouseButton.new()
	up.button_index = MOUSE_BUTTON_LEFT
	up.position = start + delta
	up.pressed = false
	map._gui_input(up)


func _show_control_ancestors(node: Node, stop_at: Node) -> void:
	var current := node
	while current != null:
		_set_visible_if_canvas_item(current, true)
		if current == stop_at:
			break
		current = current.get_parent()


func _set_visible_if_canvas_item(node: Node, visible: bool) -> void:
	if node is CanvasItem:
		(node as CanvasItem).visible = visible


func _missing_node(role: String, expected_names: Array, fallback_texts: Array = []) -> bool:
	var available_controls := _describe_available_controls(get_root())
	_record_failure("UI_VISUAL_CAPTURE_MISSING_NODE: state=%s role=%s names=%s fallback_texts=%s available_controls=%s" % [
		capture_state,
		role,
		expected_names,
		fallback_texts,
		available_controls,
	])
	return false


func _describe_available_controls(root: Node) -> Array[String]:
	var descriptions: Array[String] = []
	for child in root.find_children("*", "Control", true, false):
		if not child is Control:
			continue
		var description := "%s:%s" % [child.name, child.get_class()]
		if child is Button:
			var text := (child as Button).text.strip_edges()
			if text != "":
				description += " text='%s'" % text
		descriptions.append(description)
		if descriptions.size() >= 24:
			descriptions.append("...")
			break
	return descriptions


func _save_capture() -> bool:
	var viewport_texture := get_root().get_texture()
	if viewport_texture == null:
		_record_failure("UI_VISUAL_CAPTURE_NO_RENDER_TEXTURE: current display driver cannot capture a frame")
		return false
	var image := viewport_texture.get_image()
	if image == null or image.is_empty():
		_record_failure("UI_VISUAL_CAPTURE_NO_IMAGE: current display driver cannot capture a frame")
		return false
	if image.get_width() != capture_size.x or image.get_height() != capture_size.y:
		_record_failure("UI_VISUAL_CAPTURE_SIZE_MISMATCH: expected=%dx%d actual=%dx%d" % [
			capture_size.x,
			capture_size.y,
			image.get_width(),
			image.get_height(),
		])
		return false
	var absolute := ProjectSettings.globalize_path(output_path)
	var directory_error := DirAccess.make_dir_recursive_absolute(absolute.get_base_dir())
	if directory_error != OK:
		_record_failure("UI_VISUAL_CAPTURE_DIRECTORY_FAILED: path=%s error=%s" % [absolute.get_base_dir(), directory_error])
		return false
	var save_error := image.save_png(absolute)
	if save_error != OK:
		_record_failure("UI_VISUAL_CAPTURE_SAVE_FAILED: path=%s error=%s" % [absolute, save_error])
		return false
	print("UI_VISUAL_CAPTURE_SAVED: " + absolute)
	return true


func _record_failure(message: String) -> void:
	if failure_message == "":
		failure_message = message
	push_error(message)


func _finish_failure(exit_code: int) -> void:
	if finished:
		return
	finished = true
	print("UI_VISUAL_CAPTURE_FAILED: " + (failure_message if failure_message != "" else "unknown error"))
	quit(exit_code)


func _on_timeout() -> void:
	if finished:
		return
	_record_failure("UI_VISUAL_CAPTURE_TIMEOUT")
	_finish_failure(5)
