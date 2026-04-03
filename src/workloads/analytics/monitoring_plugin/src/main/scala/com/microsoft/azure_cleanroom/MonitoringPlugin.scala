package com.microsoft.azure_cleanroom

import org.apache.spark.api.plugin.{DriverPlugin, ExecutorPlugin, SparkPlugin, PluginContext}
import org.apache.spark.{TaskContext, TaskFailedReason}
import org.apache.spark.internal.Logging

import io.opentelemetry.api.GlobalOpenTelemetry
import io.opentelemetry.context.{Context, Scope}
import io.opentelemetry.context.propagation.TextMapGetter

import java.util.{HashMap => JHashMap}

object Constants {
  val SPARK_OTEL_TRACEPARENT_KEY = "spark.otel.traceparent"
  val TRACEPARENT_KEY = "traceparent"
}

object OtelUtils {
  val traceparentGetter: TextMapGetter[java.util.Map[String, String]] = 
    new TextMapGetter[java.util.Map[String, String]] {
      override def get(carrier: java.util.Map[String, String], key: String): String =
        carrier.get(key)

      override def keys(carrier: java.util.Map[String, String]): java.lang.Iterable[String] =
        carrier.keySet()
    }

  def extractContextFromCarrier(carrier: java.util.Map[String, String]): Context = {
    val propagator = GlobalOpenTelemetry.getPropagators.getTextMapPropagator
    propagator.extract(Context.current(), carrier, traceparentGetter)
  }
}

class MonitoringPlugin extends SparkPlugin {

  override def driverPlugin(): DriverPlugin = new MonitoringDriverPlugin

  override def executorPlugin(): ExecutorPlugin = new MonitoringExecutorPlugin
}

class MonitoringDriverPlugin extends DriverPlugin with Logging {
  
  private var driverScope: Scope = _

  override def init(ctx: org.apache.spark.SparkContext, pluginContext: PluginContext): java.util.Map[String, String] = {
    logInfo(s"Driver plugin initialized ${ctx.appName}")

    val traceparent = ctx.getConf.get(Constants.SPARK_OTEL_TRACEPARENT_KEY, null)
    if (traceparent != null) {
      logInfo("Found traceparent key in Spark conf")
      val carrier = new JHashMap[String, String]()
      carrier.put(Constants.TRACEPARENT_KEY, traceparent)

      val extracted = OtelUtils.extractContextFromCarrier(carrier)

      driverScope = extracted.makeCurrent()
      logInfo("Driver context set from Spark conf")

      carrier
    } else {
      logInfo("No traceparent found in Spark conf")
      new JHashMap[String, String]()
    }
  }

  override def shutdown(): Unit = {
    if (driverScope != null) {
      try {
        driverScope.close()
        logInfo("Driver context cleaned up")
      } catch {
        case e: Exception => logError(s"Error closing driver scope: ${e.getMessage}")
      }
    }
  }
}

class MonitoringExecutorPlugin extends ExecutorPlugin with Logging {

  private val scopeTL = new ThreadLocal[Scope]()
  private var extractedContext: Context = _

  override def init(ctx: PluginContext, extraConf: java.util.Map[String, String]): Unit = {
    logInfo("Executor plugin initialized")
    extractedContext = OtelUtils.extractContextFromCarrier(extraConf)
 }

  override def onTaskStart(): Unit = {
    logInfo(s"Executor plugin invoked for task ${TaskContext.get.taskAttemptId}")

    if (extractedContext == null) return

    val scope = extractedContext.makeCurrent()
    logInfo(s"Context set for task ${TaskContext.get.taskAttemptId}")
    scopeTL.set(scope)
  }

  override def onTaskSucceeded(): Unit = cleanup()
  override def onTaskFailed(failureReason: TaskFailedReason): Unit = cleanup()
  override def shutdown(): Unit = cleanup()

  private def cleanup(): Unit = {
    Option(scopeTL.get()).foreach { scope =>
      try scope.close() finally scopeTL.remove()
    }
  }
}
