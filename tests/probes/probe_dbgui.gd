extends Node
# Разведка/регрессия геометрии дебаг-UI: панель фиксированной высоты, кнопки
# внутри ScrollContainer (много кнопок не растягивают панель за экран), инфо
# вынесено в отдельное окно DebugInfoPanel по кнопке «Debug Info».
func _ready() -> void:
	var win := get_window()
	print("WINDOW size=", win.size)
	var layer: CanvasLayer = load("res://scenes/DebugLayer.tscn").instantiate()
	add_child(layer)
	await get_tree().process_frame
	layer.ToggleDebugMode()
	layer.TogglePanel()
	# 12 кнопок — заведомо больше, чем помещается в панель 256 px.
	var buttons_host: VBoxContainer = layer.get_node("DebugRoot/DebugPanel/VBox/Scroll/Buttons")
	for i in 12:
		var b := Button.new()
		b.name = "Btn%02d" % i
		b.text = "Btn %02d" % i
		buttons_host.add_child(b)
	await get_tree().process_frame

	var panel: PanelContainer = layer.get_node("DebugRoot/DebugPanel")
	var vbox: VBoxContainer = layer.get_node("DebugRoot/DebugPanel/VBox")
	var scroll: ScrollContainer = layer.get_node("DebugRoot/DebugPanel/VBox/Scroll")
	var buttons: VBoxContainer = layer.get_node("DebugRoot/DebugPanel/VBox/Scroll/Buttons")
	var chk: CheckButton = layer.get_node("DebugRoot/DebugPanel/VBox/HBoxTop/ChkPauseWorld")
	var btnInfo: Button = layer.get_node("DebugRoot/DebugPanel/VBox/HBoxTop/BtnDebugInfo")
	var infoPanel: PanelContainer = layer.get_node("DebugRoot/DebugInfoPanel")
	var lbl: Label = layer.get_node("DebugRoot/DebugInfoPanel/LblDebugInfo")

	print("panel pos=", panel.position, " size=", panel.size, " minsize=", panel.get_combined_minimum_size())
	print("vbox size=", vbox.size)
	print("scroll size=", scroll.size, " buttons minsize=", buttons.get_combined_minimum_size(),
		  " child count=", buttons.get_child_count())
	print("chk pos=", chk.position, " size=", chk.size, " text='", chk.text, "'")
	print("btnInfo text='", btnInfo.text, "' toggle=", btnInfo.toggle_mode)
	print("infoPanel visible(before)=", infoPanel.visible)
	btnInfo.button_pressed = true  # эмуляция нажатия: сигнал toggled -> ToggleInfoPanel
	await get_tree().process_frame
	print("infoPanel visible(after)=", infoPanel.visible, " pos=", infoPanel.position,
		  " size=", infoPanel.size)
	print("lbl size=", lbl.size, " minsize=", lbl.get_combined_minimum_size())
	# Кнопки после раскрытия инфо-окна не должны сдвигаться: панель той же высоты.
	print("panel size after info=", panel.size)
	get_tree().quit(0)
