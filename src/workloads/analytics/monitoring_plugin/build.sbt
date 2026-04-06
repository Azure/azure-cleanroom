/****************************************
 *  spark‑plugins – build definition    *
 ****************************************/

// ─── Versions ────────────────────────────────────────────────────────────────
val sparkVersion   = "4.0.0"
val scala213       = "2.13.15"
val otelVersion    = "1.34.0"


// ─── Project coordinates ────────────────────────────────────────────────────
name                 := "cleanroom-monitoring-plugin"
organization         := "com.microsoft.azure_cleanroom"
version              := "1.0.0"
scalaVersion         := scala213
crossScalaVersions   := Seq(scala213)
publishMavenStyle    := true

// ─── Dependencies ────────────────────────────────────────────────────────────
// NOTE: Spark and OpenTelemetry libraries are marked 'provided'
// because they are included separately in the container image classpath.
libraryDependencies ++= Seq(
  "org.apache.spark"            %% "spark-core"            % sparkVersion % Provided,
  "org.apache.spark"            %% "spark-sql"             % sparkVersion % Provided,
  "io.opentelemetry"            % "opentelemetry-api"      % otelVersion  % Provided,
  "io.opentelemetry"            % "opentelemetry-context"  % otelVersion  % Provided
)

// ─── Project metadata ─────────────────────────────────────────────────────────
organization := "com.microsoft.azure_cleanroom"
description := "Use Spark Plugins to extend Apache Spark with custom metrics and executors' startup actions."

homepage   := Some(url("https://github.com/azure/azure-cleanroom"))
licenses   += ("Apache-2.0", url("http://www.apache.org/licenses/LICENSE-2.0"))
developers := List(
  Developer(
    id    = "azcleanroomdev",
    name  = "Azure Cleanroom Dev Team",
    email = "azurecleanroomdev@microsoft.com",
     url("https://github.com/microsoft")
  )
)

// ─── Assembly settings ────────────────────────────────────────────────────────
import sbtassembly.AssemblyPlugin.autoImport._
import java.nio.file.Files

assembly / assemblyOutputPath := {
  val outputDir = baseDirectory.value / "dist"
  Files.createDirectories(outputDir.toPath)
  outputDir / "cleanroom-monitoring-plugin.jar"
}

assembly / assemblyMergeStrategy := {
  case PathList("META-INF", "MANIFEST.MF") => MergeStrategy.discard
  case PathList("META-INF", xs @ _*) => MergeStrategy.first
  case _ => MergeStrategy.first}