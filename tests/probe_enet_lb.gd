extends Node
# Проба: два ENet-пира внутри одного процесса (headless). Соединяются ли они?
var _t := 0
var _srv := ENetMultiplayerPeer.new()
var _cli := ENetMultiplayerPeer.new()

func _ready() -> void:
	var err_s := _srv.create_server(24599, 4, 3, 0, 0)
	print("server create: ", err_s, " id=", _srv.get_unique_id())
	multiplayer.multiplayer_peer = _srv

	var err_c := _cli.create_client("127.0.0.1", 24599, 3, 0, 0, 0)
	print("client create: ", err_c, " id=", _cli.get_unique_id())
	print("mp self=", multiplayer.get_unique_id(), " peers=", multiplayer.get_peers())

func _process(_d: float) -> void:
	multiplayer.multiplayer_peer.poll()
	_cli.poll()
	_t += 1
	if _t % 20 == 0:
		print("t=", _t, " self=", multiplayer.get_unique_id(),
			  " peers=", multiplayer.get_peers(),
			  " srv_id=", _srv.get_unique_id(), " cli_id=", _cli.get_unique_id(),
			  " cli_peers=", _cli.get_peers())
	if _t >= 120:
		print("DONE: peers=", multiplayer.get_peers())
		get_tree().quit(0)
