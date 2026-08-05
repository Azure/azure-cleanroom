"""Emit the executed Spark SQL query plan for debugging.

The executed (post-AQE) physical plan and a per-stage shuffle/spill breakdown
are logged at the end of every run (success or failure) via the application
logger, which is exported over OTLP to Loki tagged by the
``{job_id}-analytics-app`` service.
"""

import json
import urllib.request
from datetime import datetime

_MAX_PLAN_BYTES = 128 * 1024


def _cap(text):
    data = text.encode("utf-8")
    if len(data) <= _MAX_PLAN_BYTES:
        return text
    truncated = data[:_MAX_PLAN_BYTES].decode("utf-8", "ignore")
    return f"{truncated}\n\u2026[truncated {len(data) - _MAX_PLAN_BYTES} bytes]"


def _sql_conf(conf, key):
    try:
        return conf.getConfString(key)
    except Exception:
        return "<unset>"


def _stage_list(spark, logger):
    ui_url = spark.sparkContext.uiWebUrl
    if not ui_url:
        return []
    url = f"{ui_url}/api/v1/applications/{spark.sparkContext.applicationId}/stages"
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:
            return json.loads(resp.read().decode())
    except (OSError, ValueError) as e:
        logger.warning("QueryPlan stage list REST fetch failed (ignored): %s", e)
        return []


def _duration_ms(submission, completion):
    start, end = _iso_ms(submission), _iso_ms(completion)
    return int(end - start) if start is not None and end is not None else -1


def _iso_ms(value):
    if not value:
        return None
    try:
        parsed = datetime.strptime(
            value.replace("GMT", "+0000"), "%Y-%m-%dT%H:%M:%S.%f%z"
        )
        return parsed.timestamp() * 1000
    except ValueError:
        return None


def dump_query_plan(spark, job_id, logger):
    try:
        status_store = spark._jsparkSession.sharedState().statusStore()

        conf = spark._jsparkSession.sessionState().conf()
        logger.info(
            "QueryPlan job_id=%s config shuffle.partitions=%s adaptive.enabled=%s "
            "autoBroadcastJoinThreshold=%s",
            job_id,
            _sql_conf(conf, "spark.sql.shuffle.partitions"),
            _sql_conf(conf, "spark.sql.adaptive.enabled"),
            _sql_conf(conf, "spark.sql.autoBroadcastJoinThreshold"),
        )

        # Executed (post-AQE) physical plan from the last SQL execution. The SQL
        # status store's physicalPlanDescription is updated when adaptive
        # execution finalizes, so it carries the AQEShuffleRead coalesce/skew
        # nodes and per-operator metrics.
        execs = status_store.executionsList()
        if execs.size() > 0:
            plan = execs.apply(execs.size() - 1).physicalPlanDescription()
            logger.info(
                "QueryPlan job_id=%s executed physical plan (post-AQE):\n%s",
                job_id,
                _cap(plan),
            )
            logger.info(
                "QueryPlan job_id=%s AQEShuffleRead=%d coalesced=%d skewed=%d",
                job_id,
                plan.count("AQEShuffleRead"),
                plan.count("coalesced"),
                plan.count("skewed"),
            )
        else:
            logger.info("QueryPlan job_id=%s no SQL execution recorded", job_id)

        # A stage is "interesting" if it failed or did real work
        # (scan/compute/shuffle/spill) - a slow scan- or compute-bound stage has
        # no shuffle, so input/output records are checked too, otherwise the
        # bottleneck stage of a slow query would be skipped.
        for st in _stage_list(spark, logger):
            status = str(st.get("status", ""))
            failed = status.upper() == "FAILED"
            if not (
                failed
                or st.get("shuffleReadRecords")
                or st.get("shuffleWriteRecords")
                or st.get("diskBytesSpilled")
                or st.get("memoryBytesSpilled")
                or st.get("inputRecords")
                or st.get("outputRecords")
            ):
                continue
            logger.info(
                "QueryPlan StageStats job_id=%s stage=%s attempt=%s status=%s "
                "durationMs=%d tasks=%s failedTasks=%s inputRecords=%s "
                "outputRecords=%s shuffleRead=%s shuffleWrite=%s diskSpill=%s "
                "memSpill=%s name=%s",
                job_id,
                st.get("stageId"),
                st.get("attemptId"),
                status,
                _duration_ms(st.get("submissionTime"), st.get("completionTime")),
                st.get("numTasks"),
                st.get("numFailedTasks"),
                st.get("inputRecords"),
                st.get("outputRecords"),
                st.get("shuffleReadRecords"),
                st.get("shuffleWriteRecords"),
                st.get("diskBytesSpilled"),
                st.get("memoryBytesSpilled"),
                st.get("name"),
            )

            if failed and st.get("failureReason"):
                logger.error(
                    "QueryPlan StageFailure job_id=%s stage=%s reason=%s",
                    job_id,
                    st.get("stageId"),
                    st.get("failureReason"),
                )
    except Exception as e:
        logger.warning("QueryPlan dump failed (ignored): %s", e)


def dump_formatted_plan(df, job_id, logger):
    """
    FORMATTED is Spark's recommended human-readable plan form; it complements
    the post-AQE runtime plan captured by dump_query_plan.
    """
    try:
        query_execution = df._jdf.queryExecution()
        mode = df._sc._jvm.org.apache.spark.sql.execution.ExplainMode.fromString(
            "formatted"
        )
        logger.info(
            "QueryPlan job_id=%s formatted plan:\n%s",
            job_id,
            _cap(query_execution.explainString(mode)),
        )
    except Exception as e:
        logger.warning("QueryPlan formatted plan dump failed (ignored): %s", e)
