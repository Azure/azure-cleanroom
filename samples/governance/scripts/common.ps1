function GetPortOrDie([string] $port) {
  if ($port -ne "") {
    return $port
  }

  if (-not $env:CGSCLIENT_PORT) {
    throw 'Error: -port argument or CGSCLIENT_PORT environment variable must be set.'
  }

  return $env:CGSCLIENT_PORT
}