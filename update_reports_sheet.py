import glob
import os
import openpyxl
import pandas as pd

EXCEL_PATH = "TCC_Graficos_Performance_Recursos.xlsx"
BASE_DIR = "reports"  # pasta onde ficam multimodel/ e heterogeneous/

ARCH_MAP = {"multimodel": "Multimodelo", "heterogeneous": "Heterogênea"}
SCEN_MAP = {"low": "Low", "medium": "Medium", "high": "High", "higher": "Higher"}
THEO_RPS = {"Low": 499.0, "Medium": 1000.0, "High": 1999.0, "Higher": 4001.0}

# Mapeamento fixo das linhas da aba Base_Execucoes (linhas 5 a 28)
ROW_MAP = {}
r = 5
for arch in ["Multimodelo", "Heterogênea"]:
    for scen in ["Low", "Medium", "High", "Higher"]:
        for rep in ["Rep 1", "Rep 2", "Rep 3"]:
            ROW_MAP[(arch, scen, rep)] = r
            r += 1


def clear_old_data(wb):
    # 1. Base_Execucoes: limpa apenas as colunas de entrada (linhas 5 a 28),
    # preservando metadados (B:E) e fórmulas (A, J, M)
    ws_base = wb["Base_Execucoes"]
    colunas_entrada = [6, 7, 8, 9, 11, 12] + list(range(14, 26))  # F:I, K:L, N:Y
    for row in range(5, 29):
        for col in colunas_entrada:
            ws_base.cell(row=row, column=col).value = None

    # 2. Detalhes_Subcenarios: limpa os registros detalhados (a partir da linha 14),
    # preservando a tabela de resumo AVERAGEIFS (linhas 5 a 10)
    ws_sub = wb["Detalhes_Subcenarios"]
    for row in range(14, ws_sub.max_row + 1):
        for col in range(1, 12):  # A:K
            ws_sub.cell(row=row, column=col).value = None

    # 3. Uso_Recursos_Timeline: limpa toda a série temporal (a partir da linha 5)
    ws_tl = wb["Uso_Recursos_Timeline"]
    for row in range(5, ws_tl.max_row + 1):
        for col in range(1, 12):  # A:K
            ws_tl.cell(row=row, column=col).value = None

def parse_resource_usage(csv_path, warmup_s=30):
    df = pd.read_csv(csv_path, header=None, names=["timestamp", "container", "cpu", "mem"])
    df["dt"] = pd.to_datetime(df["timestamp"], utc=True)
    df["elapsed_s"] = (df["dt"] - df["dt"].min()).dt.total_seconds()
    active = df[df["elapsed_s"] >= warmup_s].copy()
    if active.empty:
        active = df.copy()

    out = {}

    # 1. Camada de API (ebenezer-api)
    api = active[active["container"] == "ebenezer-api"]
    if not api.empty:
        out["api"] = {
            "cpu_mean": round(float(api["cpu"].mean()), 2),
            "cpu_max": round(float(api["cpu"].max()), 2),
            "mem_mean": round(float(api["mem"].mean()), 2),
            "mem_max": round(float(api["mem"].max()), 2),
        }

    # 2. Camada de Banco de Dados
    dbs = active[active["container"] != "ebenezer-api"].copy()
    db_containers = dbs["container"].unique().tolist()

    if len(db_containers) == 1:
        # Multimodelo: 1 único contêiner de banco (arangodb)
        out["db"] = {
            "cpu_mean": round(float(dbs["cpu"].mean()), 2),
            "cpu_max": round(float(dbs["cpu"].max()), 2),
            "mem_mean": round(float(dbs["mem"].mean()), 2),
            "mem_max": round(float(dbs["mem"].max()), 2),
        }
    elif len(db_containers) >= 2:
        # Heterogênea: soma instantânea de ebenezer-mongodb + ebenezer-neo4j no mesmo ciclo
        mongo = dbs[dbs["container"].str.contains("mongo", case=False)].sort_values("dt")
        neo4j = dbs[dbs["container"].str.contains("neo4j", case=False)].sort_values("dt")

        if not mongo.empty and not neo4j.empty:
            paired = pd.merge_asof(
                mongo[["dt", "cpu", "mem"]].rename(columns={"cpu": "cpu_m", "mem": "mem_m"}),
                neo4j[["dt", "cpu", "mem"]].rename(columns={"cpu": "cpu_n", "mem": "mem_n"}),
                on="dt",
                direction="nearest",
                tolerance=pd.Timedelta(seconds=4),
            ).dropna()

            if not paired.empty:
                cpu_total = paired["cpu_m"] + paired["cpu_n"]
                mem_total = paired["mem_m"] + paired["mem_n"]
                out["db"] = {
                    "cpu_mean": round(float(cpu_total.mean()), 2),
                    "cpu_max": round(float(cpu_total.max()), 2),
                    "mem_mean": round(float(mem_total.mean()), 2),
                    "mem_max": round(float(mem_total.max()), 2),
                }

    return out


wb = openpyxl.load_workbook(EXCEL_PATH)
clear_old_data(wb)
ws_base = wb["Base_Execucoes"]
input_cols = [6, 7, 8, 9, 11, 12] + list(range(14, 26))

for arch_dir, arch_label in ARCH_MAP.items():
    for scen_prefix, scen_label in SCEN_MAP.items():
        pattern = os.path.join(BASE_DIR, arch_dir, f"{scen_prefix}_all_*")
        folders = sorted(glob.glob(pattern))[-3:]

        for idx, folder in enumerate(folders):
            nb_files = glob.glob(os.path.join(folder, "nbomber_report_*.csv"))
            if not nb_files:
                continue

            rep_label = f"Rep {idx + 1}"
            row_num = ROW_MAP[(arch_label, scen_label, rep_label)]

            for col_num in input_cols:
                ws_base.cell(row_num, col_num).value = None

            nb_file = nb_files[0]
            df_nb = pd.read_csv(nb_file)
            g = df_nb[df_nb["step_name"] == "global information"]
            s = df_nb[df_nb["step_name"] != "global information"]

            h, m, sec = [int(x) for x in str(g["duration"].iloc[0]).split(":")]
            dur_s = h * 3600 + m * 60 + sec

            tp_file = os.path.join(folder, "throughput.csv")
            if os.path.exists(tp_file):
                df_tp = pd.read_csv(tp_file)
                tot = df_tp[df_tp["scenario"] == "TOTAL"].iloc[0]
                theo_rps = float(tot["theoretical_requests_per_second"])
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

            res_file = os.path.join(folder, "resource-usage.csv")
            if os.path.exists(res_file):
                res = parse_resource_usage(res_file)
                if "api" in res:
                    ws_base.cell(row_num, 18, res["api"]["cpu_mean"])
                    ws_base.cell(row_num, 19, res["api"]["cpu_max"])
                    ws_base.cell(row_num, 20, res["api"]["mem_mean"])
                    ws_base.cell(row_num, 21, res["api"]["mem_max"])
                if "db" in res:
                    ws_base.cell(row_num, 22, res["db"]["cpu_mean"])
                    ws_base.cell(row_num, 23, res["db"]["cpu_max"])
                    ws_base.cell(row_num, 24, res["db"]["mem_mean"])
                    ws_base.cell(row_num, 25, res["db"]["mem_max"])

            print(f"Atualizado: linha {row_num} ({arch_label} | {scen_label} | {rep_label}) -> {os.path.basename(nb_file)}")

wb.save(EXCEL_PATH)
print("Planilha atualizada com sucesso!")