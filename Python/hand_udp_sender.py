"""
hand_2cams_udp.py (versión mejorada reconstrucción 3D aproximada)
- Usa 2 cámaras: frontal (X,Y) y lateral (Z,Y)
- Normaliza y centra la cámara lateral para dar volumen
- Envía UDP JSON con landmarks y grab
"""

import cv2
import json
import socket
import mediapipe as mp
import time
import sys

# ====== CONFIG ======
HOST = "127.0.0.1"
PORT = 5005
MIRROR = True
DRAW = True
WIDTH, HEIGHT = 640, 480
MIN_DETECTION_CONF = 0.45
MIN_TRACKING_CONF = 0.4
DEPTH_SCALE = 3.0  # escala de profundidad (Z)
# =====================

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

mp_hands = mp.solutions.hands
mp_draw = mp.solutions.drawing_utils
mp_styles = mp.solutions.drawing_styles

# ----- Funciones para abrir cámaras -----
def try_open_camera(index):
    cap = cv2.VideoCapture(index)
    if cap is not None and cap.isOpened():
        ret, _ = cap.read()
        if ret:
            return cap
    return None

def find_two_cameras(max_index=6):
    found = []
    for i in range(max_index):
        cap = try_open_camera(i)
        if cap:
            found.append((i, cap))
            if len(found) >= 2:
                break
    return found

# ---- Inicializar cámaras ----
found = find_two_cameras(max_index=6)
if len(found) < 2:
    print("❌ No se detectaron dos cámaras.")
    sys.exit(1)

(front_idx, cap_front), (side_idx, cap_side) = found[0], found[1]
cap_front.set(cv2.CAP_PROP_FRAME_WIDTH, WIDTH)
cap_front.set(cv2.CAP_PROP_FRAME_HEIGHT, HEIGHT)
cap_side.set(cv2.CAP_PROP_FRAME_WIDTH, WIDTH)
cap_side.set(cv2.CAP_PROP_FRAME_HEIGHT, HEIGHT)

hands_front = mp_hands.Hands(
    static_image_mode=False,
    max_num_hands=1,
    min_detection_confidence=MIN_DETECTION_CONF,
    min_tracking_confidence=MIN_TRACKING_CONF,
    model_complexity=1
)
hands_side = mp_hands.Hands(
    static_image_mode=False,
    max_num_hands=1,
    min_detection_confidence=MIN_DETECTION_CONF,
    min_tracking_confidence=MIN_TRACKING_CONF,
    model_complexity=1
)

print(f"Enviando UDP a {HOST}:{PORT} ... (ESC para salir)")

# ---- Funciones auxiliares ----
def get_confidence(results):
    try:
        if results.multi_handedness:
            return results.multi_handedness[0].classification[0].score
    except:
        pass
    return 0.0

# ---- Loop principal ----
try:
    while True:
        ok1, frame_front = cap_front.read()
        ok2, frame_side = cap_side.read()
        if not ok1 or not ok2:
            print("⚠️ No se pudo leer una de las cámaras. Saliendo.")
            break

        display_front = frame_front.copy()
        display_side = frame_side.copy()
        if MIRROR:
            display_front = cv2.flip(display_front, 1)
            display_side = cv2.flip(display_side, 1)

        rgb_front = cv2.cvtColor(frame_front, cv2.COLOR_BGR2RGB)
        rgb_side = cv2.cvtColor(frame_side, cv2.COLOR_BGR2RGB)

        results_front = hands_front.process(rgb_front)
        results_side = hands_side.process(rgb_side)

        handF = results_front.multi_hand_landmarks[0] if results_front.multi_hand_landmarks else None
        handS = results_side.multi_hand_landmarks[0] if results_side.multi_hand_landmarks else None

        # detectar agarre
        grab = False
        if handF:
            t = handF.landmark
            dist_thr = 0.06
            if any(
                ((t[4].x - t[i].x)**2 + (t[4].y - t[i].y)**2)**0.5 < dist_thr
                for i in [8,12,16,20]
            ):
                grab = True

        # reconstruir landmarks 3D
        landmarks = []
        for i in range(21):
            # frontal
            x_f = handF.landmark[i].x if handF else 0.0
            y_f = handF.landmark[i].y if handF else 0.0
            z_f = handF.landmark[i].z if handF else 0.0
            # lateral
            x_s = handS.landmark[i].x if handS else None
            y_s = handS.landmark[i].y if handS else None

            # X = frontal
            X = float(x_f)
            # Y = promedio de ambas cámaras
            if y_s is not None:
                Y = float((y_f + y_s) / 2.0)
            else:
                Y = float(y_f)
            # Z = reconstrucción 3D aproximada
            if x_s is not None:
                Z = float((0.5 - x_s) * DEPTH_SCALE)  # centrar y escalar
            else:
                Z = float(z_f)

            landmarks.append({"x": X, "y": Y, "z": Z})

        payload = {"landmarks": landmarks, "grab": grab}

        # enviar UDP
        try:
            data = json.dumps(payload).encode("utf-8")
            sock.sendto(data, (HOST, PORT))
        except Exception as e:
            print(f"Error enviando UDP: {e}")

        # dibujar para debug
        if DRAW:
            if handF:
                mp_draw.draw_landmarks(display_front, handF, mp_hands.HAND_CONNECTIONS,
                                       mp_styles.get_default_hand_landmarks_style(),
                                       mp_styles.get_default_hand_connections_style())
            if handS:
                mp_draw.draw_landmarks(display_side, handS, mp_hands.HAND_CONNECTIONS,
                                       mp_styles.get_default_hand_landmarks_style(),
                                       mp_styles.get_default_hand_connections_style())

        cv2.putText(display_front, f"Frontal ({front_idx})", (10,30),
                    cv2.FONT_HERSHEY_SIMPLEX,1,(255,255,255),2)
        cv2.putText(display_side, f"Lateral ({side_idx})", (10,30),
                    cv2.FONT_HERSHEY_SIMPLEX,1,(255,255,255),2)

        cv2.imshow("Camara Frontal", display_front)
        cv2.imshow("Camara Lateral", display_side)

        key = cv2.waitKey(1) & 0xFF
        if key == 27:
            break

except KeyboardInterrupt:
    print("Saliendo por teclado...")

finally:
    try:
        cap_front.release()
        cap_side.release()
    except:
        pass
    hands_front.close()
    hands_side.close()
    cv2.destroyAllWindows()
    print("Cerrado.")




