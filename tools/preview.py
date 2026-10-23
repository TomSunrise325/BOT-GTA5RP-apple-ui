"""Mock server to preview the redesigned UI without main.exe."""
import sys, pathlib, json
here = pathlib.Path(__file__).parent
sys.path.insert(0, str(here / "libs"))
from flask import Flask, render_template, jsonify, Response, request

root = here.parent / "bot-src" / "bot"
app = Flask(__name__, template_folder=str(root / "templates"), static_folder=str(root / "static"))
app.config["TEMPLATES_AUTO_RELOAD"] = True
app.config["SEND_FILE_MAX_AGE_DEFAULT"] = 0

UI = dict(switch_hover=True, switch_click=False, volume_hover=35, volume_click=45, background="bot")
BINDS = [
    dict(id=1, name="Авто-еда", hotkey="f6", keys=["k"], action_type="press", repeat=True, cooldown=30),
    dict(id=2, name="Бег", hotkey="f7", keys=["shift", "w"], action_type="hold", hold_duration=3, repeat=False),
]
SETTINGS = dict(UI, min_delay=0.8, max_delay=2.4, min_pause=8, max_pause=34, volume=70, sound_type="yandex",
                hotkey_kpk="F5", auto_e_hotkey="f5", food_key="k", food_pause=1800, auto_e_pause=5.0,
                walker_hotkey="f6", pause=0.15, selected_dish="Фруктовый салат", binds=BINDS)

def ctx(**kw):
    base = dict(ui_sounds=UI, settings=SETTINGS, online=1284, version="2.4.1", active=False, counter=0,
                recipes=["Фруктовый салат", "Мясо на огне", "Овощной суп"], vgamepad_available=("nodrv" not in request.args),
                update_info=None)
    base.update(kw)
    return base

for name in ["index", "antiafk", "demorgan", "stroyka", "port", "cow", "gym", "cooking", "kpk", "bonuspoint", "keybinds", "settings"]:
    def make(n):
        return lambda: render_template(n + ".html", **ctx(active=("run" in request.args)))
    app.add_url_rule("/" + name, name, make(name))
app.add_url_rule("/", "root", lambda: render_template("index.html", **ctx()))

LOGS = ["[gym] Модуль запущен", "[gym] Найден зелёный круг, нажимаю пробел", "[gym] Ошибка распознавания, повтор", "[gym] Использована еда"]

@app.route("/api/get_logs")
def logs(): return jsonify(logs=LOGS)

@app.route("/api/settings/get")
def sget(): return jsonify(UI)

@app.route("/api/bp/get_state")
def bps(): return jsonify(vip=True, server_x2=False)

@app.route("/api/bp/get_tasks")
def bpt(): return jsonify([dict(name=n, base=b, vip=b * 2, checked=c) for n, b, c in
    [("Отработать смену на стройке", 3, True), ("Приготовить 5 блюд", 2, False), ("Заняться в качалке 20 минут", 4, True),
     ("Выполнить заказ на порту", 3, False), ("Доставить груз", 2, False), ("Починить автомобиль", 2, True), ("Вылечить пациента", 3, False)]])

@app.route("/api/events")
def events(): return Response("retry: 600000\n\n", mimetype="text/event-stream")

@app.route("/api/<path:p>", methods=["GET", "POST"])
def anyapi(p): return jsonify({})

if __name__ == "__main__":
    app.run(port=5057, debug=False, threaded=True)

