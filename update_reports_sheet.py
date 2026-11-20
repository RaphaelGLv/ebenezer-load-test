import glob, os
import pandas as pd
import openpyxl

EXCEL_PATH = "TCC_Graficos_Performance_Recursos.xlsx"
BASE_DIR = "reports"  # pasta onde ficam multimodel/ e heterogeneous/

ARCH_MAP = {"multimodel": "Multimodelo", "heterogeneous": "Heterogênea"}
SCEN_MAP = {"low": "Low", "medium": "Medium", "high": "High", "higher": "Higher"}
THEO_RPS = {"Low": 499.0, "Medium": 1000.0, "High": 1999.0, "Higher": 3000.0}

# Mapeamento fixo das linhas da aba Base_Execucoes (linhas 5 a 28)
ROW_MAP = {}
r = 5
for arch in ["Multimodelo", "Heterogênea"]:
    for scen in ["Low", "Medium", "High", "Higher"]:
        for rep in ["Rep 1", "Rep 2", "Rep 3"]:
            ROW_MAP[(arch, scen, rep)] = r
            r += 1

def parse_resource_usage(csv_path, warmup_s=30):
    df = pd.read_csv(csv_path, header=None, names=["timestamp", "container", "cpu", "mem"])
    df["timestamp"] = pd.to_datetime(df["timestamp"])
    df["elapsed_s"] = (df["timestamp"] - df["timestamp"].min()).dt.total_seconds()
    active = df[df["elapsed_s"] >= warmup_s]
    if active.empty:
        active = df
    res = {}
    for cont, g in active.groupby("container"):
        res[cont] = {
            "cpu_mean": round(float(g["cpu"].mean()), 2),
            "cpu_max": round(float(g["cpu"].max()), 2),
            "mem_mean": round(float(g["mem"].mean()), 2),
            "mem_max": round(float(g["mem"].max()), 2),
        }
    return res

wb = openpyxl.load_workbook(EXCEL_PATH)
ws_base = wb["Base_Execucoes"]

# Limpa apenas as colunas de entrada (F..I, K..L, N..Y) antes de repovoar
input_cols = [6, 7, 8, 9, 11, 12] + list(range(14, 26))
for row_num in range(5, 29):
    for col_num in input_cols:
        ws_base.cell(row_num, col_num).value = None

for arch_dir, arch_label in ARCH_MAP.items():
    for scen_prefix, scen_label in SCEN_MAP.items():
        pattern = os.path.join(BASE_DIR, arch_dir, f"{scen_prefix}_all_*")
        # Ordena cronologicamente e pega até as 3 repetições mais recentes
        folders = sorted(glob.glob(pattern))[-3:]
        for idx, folder in enumerate(folders):
            rep_label = f"Rep {idx + 1}"
            row_num = ROW_MAP[(arch_label, scen_label, rep_label)]

            nb_files = glob.glob(os.path.join(folder, "nbomber_report_*.csv"))
            if not nb_files:
                continue
            nb_file = nb_files[0]
            df_nb = pd.read_csv(nb_file)
            g = df_nb[df_nb["step_name"] == "global information"]
            s = df_nb[df_nb["step_name"] != "global information"]

            # Duração em segundos
            h, m, sec = [int(x) for x in str(g["duration"].iloc[0]).split(":")]
            dur_s = h * 3600 + m * 60 + sec

            # Vazão real e teórica (usa throughput.csv se existir, senão calcula pelo report)
            tp_file = os.path.join(folder, "throughput.csv")
            if os.path.exists(tp_file):
                df_tp = pd.read_csv(tp_file)
                tot = df_tp[df_tp["scenario"] == "TOTAL"].iloc[0]
                theo_rps = float(tot["theoretical_requests_per_second"])
                actual_rps = float(tot["actual_requests_per_second"])
            else:
                theo_rps = THEO_RPS[scen_label]
                actual_rps = round(float(s["ok_rps"].sum()), 2)

            ws_base.cell(row_num, 6, os.path.basename(nb_file))
            ws_base.cell(row_num, 7, dur_s)
            ws_base.cell(row_num, 8, theo_rps)
            ws_base.cell(row_num, 9, actual_rps)
            ws_base.cell(row_num, 11, int(g["ok"].sum()))
            ws_base.cell(row_num, 12, int(g["failed"].sum()))
            ws_base.cell(row_num, 14, round(float(s["ok_mean"].mean()), 2))
            ws_base.cell(row_num, 15, round(float(s["ok_50_percent"].mean()), 2))
            ws_base.cell(row_num, 16, round(float(s["ok_95_percent"].mean()), 2))
            ws_base.cell(row_num, 17, round(float(s["ok_99_percent"].mean()), 2))

            # Uso de recursos (docker stats), se existir na pasta
            res_file = os.path.join(folder, "resource-usage.csv")
            if os.path.exists(res_file):
                res = parse_resource_usage(res_file)
                api = res.get("ebenezer-api", {})
                # Detecta o contêiner de banco (arangodb na multimodelo ou outros na heterogênea)
                db_keys = [k for k in res.keys() if k != "ebenezer-api"]
                if api:
                    ws_base.cell(row_num, 18, api["cpu_mean"])
                    ws_base.cell(row_num, 19, api["cpu_max"])
                    ws_base.cell(row_num, 20, api["mem_mean"])
                    ws_base.cell(row_num, 21, api["mem_max"])
                if db_keys:
                    ws_base.cell(row_num, 22, round(sum(res[k]["cpu_mean"] for k in db_keys), 2))
                    ws_base.cell(row_num, 23, round(max(res[k]["cpu_max"] for k in db_keys), 2))
                    ws_base.cell(row_num, 24, round(sum(res[k]["mem_mean"] for k in db_keys), 2))
                    ws_base.cell(row_num, 25, round(max(res[k]["mem_max"] for k in db_keys), 2))

wb.save(EXCEL_PATH)
print("Planilha atualizada com sucesso!")