extends SceneTree

# 水墨面板资产截图工具：捕获 GuidePanel / EndgameReportPanel 打开后的画面。
# 仅做只读展示验证，不推进 GameTime、不写 WorldState、不发命令。
# 用法：
#   --state=guide|endgame  选择要打开并截图的面板（默认 guide）
#   --output=<path>        PNG 输出路径（必填）
#   --size=1600x960        视口尺寸（可选，同 ui_visual_capture.gd 的取值）

const SCENE_PATH := "res://src/Ming.Godot/scenes/ui_preview.tscn"
const SUPPORTED_STATES := ["guide", "endgame"]
const PANEL_NODE_NAMES := {
	"guide": "GuidePanel",
	"endgame": "EndgameReportPanel",
}
const PANEL_ART_NAMES := {
	"guide": ["GuideArtBanner", "GuideArtPortrait"],
	"endgame": ["EndgameArtIllustration"],
}

var output_path := ""
var capture_state := "guide"
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
		_record_failure("UI_ART_PANELS_CAPTURE_SCENE_LOAD_FAILED: %s" % SCENE_PATH)
		_finish_failure(1)
		return

	var instance := scene.instantiate()
	if not instance is Control:
		_record_failure("UI_ART_PANELS_CAPTURE_ROOT_TYPE_ERROR: expected Control, got %s" % instance.get_class())
		instance.queue_free()
		_finish_failure(1)
		return

	var root := instance as Control
	get_root().size = capture_size
	root.set_size(Vector2(capture_size))
	get_root().add_child(root)
	await process_frame
	await process_frame
	await process_frame

	var panel_name: String = PANEL_NODE_NAMES[capture_state]
	var panel := root.find_child(panel_name, true, false) as Control
	if panel == null:
		_record_failure("UI_ART_PANELS_CAPTURE_MISSING_PANEL: state=%s panel=%s" % [capture_state, panel_name])
		_finish_failure(3)
		return

	if panel.has_method("Open"):
		panel.call("Open")
	else:
		panel.visible = true
		panel.move_to_front()
	await process_frame
	await process_frame

	if not panel.is_visible_in_tree():
		_record_failure("UI_ART_PANELS_CAPTURE_STATE_NOT_REACHED: state=%s panel=%s is not visible" % [capture_state, panel_name])
		_finish_failure(3)
		return

	# 校验新增的美术 TextureRect 已挂接且纹理非空（缺失则视为接线失败）。
	for art_name in PANEL_ART_NAMES[capture_state]:
		var art := panel.find_child(art_name, true, false) as TextureRect
		if art == null or art.texture == null:
			_record_failure("UI_ART_PANELS_CAPTURE_ART_MISSING: state=%s art=%s texture=%s" % [
				capture_state,
				art_name,
				"null" if art == null else ("set" if art.texture != null else "null"),
			])
			_finish_failure(3)
			return
		print("UI_ART_PANELS_CAPTURE_ART_RECT: %s rect=%s texture_size=%s" % [
			art_name,
			art.get_global_rect(),
			art.texture.get_size(),
		])
	print("UI_ART_PANELS_CAPTURE_PANEL_RECT: %s rect=%s" % [panel_name, panel.get_global_rect()])

	await process_frame
	if not _save_capture():
		_finish_failure(4)
		return

	print("UI_ART_PANELS_CAPTURE_READY: state=%s size=%dx%d" % [capture_state, capture_size.x, capture_size.y])
	finished = true
	quit(0)


func _parse_arguments() -> bool:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--output="):
			output_path = argument.trim_prefix("--output=")
		elif argument.begins_with("--state="):
			capture_state = argument.trim_prefix("--state=")
		elif argument.begins_with("--size="):
			if not _parse_capture_size(argument.trim_prefix("--size=")):
				return false

	if capture_state not in SUPPORTED_STATES:
		_record_failure("UI_ART_PANELS_CAPTURE_UNKNOWN_STATE: '%s'; supported=%s" % [capture_state, SUPPORTED_STATES])
		return false
	if output_path.strip_edges() == "":
		_record_failure("UI_ART_PANELS_CAPTURE_INVALID_OUTPUT: --output is required and must not be blank")
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
	_record_failure("UI_ART_PANELS_CAPTURE_INVALID_SIZE: '%s'" % value)
	return false


func _save_capture() -> bool:
	var viewport_texture := get_root().get_texture()
	if viewport_texture == null:
		_record_failure("UI_ART_PANELS_CAPTURE_NO_RENDER_TEXTURE: current display driver cannot capture a frame")
		return false
	var image := viewport_texture.get_image()
	if image == null or image.is_empty():
		_record_failure("UI_ART_PANELS_CAPTURE_NO_IMAGE: current display driver cannot capture a frame")
		return false
	if image.get_width() != capture_size.x or image.get_height() != capture_size.y:
		_record_failure("UI_ART_PANELS_CAPTURE_SIZE_MISMATCH: expected=%dx%d actual=%dx%d" % [
			capture_size.x,
			capture_size.y,
			image.get_width(),
			image.get_height(),
		])
		return false
	var absolute := ProjectSettings.globalize_path(output_path)
	var directory_error := DirAccess.make_dir_recursive_absolute(absolute.get_base_dir())
	if directory_error != OK:
		_record_failure("UI_ART_PANELS_CAPTURE_DIRECTORY_FAILED: path=%s error=%s" % [absolute.get_base_dir(), directory_error])
		return false
	var save_error := image.save_png(absolute)
	if save_error != OK:
		_record_failure("UI_ART_PANELS_CAPTURE_SAVE_FAILED: path=%s error=%s" % [absolute, save_error])
		return false
	print("UI_ART_PANELS_CAPTURE_SAVED: " + absolute)
	return true


func _record_failure(message: String) -> void:
	if failure_message == "":
		failure_message = message
	push_error(message)


func _finish_failure(exit_code: int) -> void:
	if finished:
		return
	finished = true
	print("UI_ART_PANELS_CAPTURE_FAILED: " + (failure_message if failure_message != "" else "unknown error"))
	quit(exit_code)


func _on_timeout() -> void:
	if finished:
		return
	_record_failure("UI_ART_PANELS_CAPTURE_TIMEOUT")
	_finish_failure(5)
