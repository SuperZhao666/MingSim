extends SceneTree

var focus_liaoxi := false

func _init() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument == "--focus-liaoxi":
			focus_liaoxi = true

	var scene := load("res://src/Ming.Godot/scenes/ui_preview.tscn") as PackedScene
	if scene == null:
		quit(1)
		return
	var root := scene.instantiate() as Control
	get_root().add_child(root)
	await process_frame
	await process_frame
	var map := root.get_node("MapView")
	if focus_liaoxi:
		map.LoadManifest("res://assets/maps/generated/ming_1629_liaoxi/map-manifest.json")
		map.SelectPlace("ningyuan")
	else:
		map.SelectPlace("beijing")
	await process_frame
	await process_frame
	print("UI_VISUAL_CAPTURE_READY: " + ("liaoxi" if focus_liaoxi else "overview"))
	quit(0)
