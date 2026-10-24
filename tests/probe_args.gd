extends SceneTree

func _initialize() -> void:
	print("ENGINE_ARGS=", OS.get_cmdline_args())
	print("USER_ARGS=", OS.get_cmdline_user_args())
	quit(0)
