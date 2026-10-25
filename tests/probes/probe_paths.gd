extends SceneTree
func _initialize() -> void:
	print("userdir=", OS.get_user_data_dir())
	print("userpath=", ProjectSettings.globalize_path("user://"))
	print("exepath=", OS.get_executable_path())
	quit()
