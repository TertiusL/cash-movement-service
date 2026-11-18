#!/bin/sh
set -eu

case "${1:-setup}" in
    setup)      scripts=bootstrap.sql,schema.sql,seed.sql ;;
    large-demo) scripts=bootstrap.sql,schema.sql,seed.sql,large-demo.sql ;;
    *) echo 'Use setup or large-demo.' >&2; exit 2 ;;
esac

# One session holds the setup lock across database creation, migrations and seed.
exec /opt/mssql-tools18/bin/sqlcmd -S db -U sa -C -b -r1 -d master -i "$scripts"
