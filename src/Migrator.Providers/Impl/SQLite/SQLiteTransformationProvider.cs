using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using Migrator.Framework;
using ForeignKeyConstraint=Migrator.Framework.ForeignKeyConstraint;

namespace Migrator.Providers.SQLite
{
    /// <summary>
    /// Summary description for SQLiteTransformationProvider.
    /// </summary>
    public class SQLiteTransformationProvider : TransformationProvider
    {
        public SQLiteTransformationProvider(Dialect dialect, SQLiteConnection connection)
            : base(dialect, null)
        {
            m_existingConnection = false;
            _connection = connection;
        }
        public SQLiteTransformationProvider(Dialect dialect, string connectionString)
            : base(dialect, connectionString)
        {
            m_existingConnection = true;
            _connection = new SQLiteConnection(_connectionString);
            _connection.ConnectionString = _connectionString;
            _connection.Open();
        }

        public override void AddForeignKey(string name, string primaryTable, string[] primaryColumns, string refTable,
                                          string[] refColumns, ForeignKeyConstraint constraint)
        {
            // NOOP Because SQLite doesn't support foreign keys
        }
        
        public override void RemoveForeignKey(string name, string table)
        {
            // NOOP Because SQLite doesn't support foreign keys
        }
        
        public override void RemoveColumn(string table, string column)
        {
            if (! (TableExists(table) && ColumnExists(table, column)))
                return;
            
            string[] origColDefs = GetColumnDefs(table);
            List<string> colDefs = new List<string>();

            foreach (string origdef in origColDefs) 
            {
                if (! ColumnMatch(column, origdef))
                    colDefs.Add(origdef);
            }
            
            string[] newColDefs = colDefs.ToArray();
            string[] newColDefsNoForeignKeys = newColDefs.Where(x => !x.Trim().StartsWith("constraint ", StringComparison.OrdinalIgnoreCase)).ToArray();
            string colDefsSql = String.Join(",", newColDefsNoForeignKeys);

            string[] colNames = ParseSqlForColumnNames(newColDefsNoForeignKeys);
            string colNamesSql = String.Join(",", colNames);
            
            AddTable(table + "_temp", null, colDefsSql);
            ExecuteQuery(String.Format("INSERT INTO {0}_temp SELECT {1} FROM {0}", table, colNamesSql));
            RemoveTable(table);
            ExecuteQuery(String.Format("ALTER TABLE {0}_temp RENAME TO {0}", table));
        }
        
        public override void RenameColumn(string tableName, string oldColumnName, string newColumnName)
        {
            if (ColumnExists(tableName, newColumnName))
                throw new MigrationException(String.Format("Table '{0}' has column named '{1}' already", tableName, newColumnName));
                
            if (ColumnExists(tableName, oldColumnName)) 
            {
                string[] columnDefs = GetColumnDefs(tableName);
                string columnDef = Array.Find(columnDefs, delegate(string col) { return ColumnMatch(oldColumnName, col); });
                
                string newColumnDef = columnDef.Replace(oldColumnName, newColumnName);
                
                AddColumn(tableName, newColumnDef);
                ExecuteQuery(String.Format("UPDATE {0} SET {1}={2}", tableName, newColumnName, oldColumnName));
                RemoveColumn(tableName, oldColumnName);
            }
        }
        
        public override void ChangeColumn(string table, Column column)
        {
            if (! ColumnExists(table, column.Name))
            {
                Logger.Warn("Column {0}.{1} does not exist", table, column.Name);
                return;
            }

            string tempColumn = "temp_" + column.Name;
            RenameColumn(table, column.Name, tempColumn);
            AddColumn(table, column);
            ExecuteQuery(String.Format("UPDATE {0} SET {1}={2}", table, column.Name, tempColumn));
            RemoveColumn(table, tempColumn);
        }

        public override bool TableExists(string table)
        {
            using (IDataReader reader =
                ExecuteQuery(String.Format("SELECT name FROM sqlite_master WHERE type='table' and name='{0}'",table)))
            {
                return reader.Read();
            }
        }
        
        public override bool ConstraintExists(string table, string name)
        {
            return false;
        }

        public override string[] GetTables()
        {
            List<string> tables = new List<string>();

            using (IDataReader reader = ExecuteQuery("SELECT name FROM sqlite_master WHERE type='table' AND name <> 'sqlite_sequence' ORDER BY name"))
            {
                while (reader.Read())
                {
                    tables.Add((string) reader[0]);
                }
            }

            return tables.ToArray();
        }
        
        public override Column[] GetColumns(string table)
        {       
            List<Column> columns = new List<Column>();
            foreach (string columnDef in GetColumnDefs(table))
            {
                string name = ExtractNameFromColumnDef(columnDef);
                // FIXME: Need to get the real type information
                Column column = new Column(name, DbType.String);
                bool isNullable = IsNullable(columnDef);
                column.ColumnProperty |= isNullable ? ColumnProperty.Null : ColumnProperty.NotNull;
                columns.Add(column);
            }
            return columns.ToArray();
        }

        public string GetSqlDefString(string table) 
        {
            string sqldef = null;
            using (IDataReader reader = ExecuteQuery(String.Format("SELECT sql FROM sqlite_master WHERE type='table' AND name='{0}'",table)))
            {
                if (reader.Read())
                {
                  sqldef = (string) reader[0];
                }
            }
            return sqldef;    
        }
        
        public string[] GetColumnNames(string table)
        {            
            return ParseSqlForColumnNames(GetSqlDefString(table));
        }

        public string[] GetColumnDefs(string table)
        {
           return ParseSqlColumnDefs(GetSqlDefString(table));
        }

        /// <summary>
        /// Turn something like 'columnName INTEGER NOT NULL' into just 'columnName'
        /// </summary>
        public string[] ParseSqlForColumnNames(string sqldef) 
        {
            string[] parts = ParseSqlColumnDefs(sqldef);
            return ParseSqlForColumnNames(parts);
        }
        
        public string[] ParseSqlForColumnNames(string[] parts) 
        {
            if (null == parts)
                return null;

            var result = new List<string>(); 
            for (int i = 0; i < parts.Length; i ++) 
            {
                var part = ExtractNameFromColumnDef(parts[i]);
                if (part.Trim().Equals("constraint", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (part.Trim().Equals("primary", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(part);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Name is the first value before the space.
        /// </summary>
        /// <param name="columnDef"></param>
        /// <returns></returns>
        public string ExtractNameFromColumnDef(string columnDef)
        {
            int idx = columnDef.IndexOf(" ");
            if (idx > 0)
            {
                return columnDef.Substring(0, idx);
            }
            return null;
        }

        public bool IsNullable(string columnDef)
        {
            return ! columnDef.Contains("NOT NULL");
        }
        
        public string[] ParseSqlColumnDefs(string sqldef) 
        {
            if (String.IsNullOrEmpty(sqldef)) 
            {
                return null;
            }
            
            sqldef = sqldef.Replace('\t', ' ');
            sqldef = sqldef.Replace(Environment.NewLine, " ");
            int start = sqldef.IndexOf("(");
            int end = sqldef.LastIndexOf(")");
            
            sqldef = sqldef.Substring(0, end);
            sqldef = sqldef.Substring(start + 1);

            List<string> cols = new List<string>();
            string[] tokens = sqldef.Split(',');
            int brackets = 0;
            string existing = "";
            foreach (string token in tokens)
            {
                brackets += token.Count(x => x == '(') - token.Count(x => x == ')');

                if (brackets == 0)
                {
                    if (string.IsNullOrWhiteSpace(existing))
                        cols.Add(token.Trim());
                    else
                        cols.Add((existing + "," + token).Trim());

                    existing = "";
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(existing))
                        existing = token;
                    else
                        existing += "," + token;
                }
            }

            return cols.ToArray();
        }
        
        public bool ColumnMatch(string column, string columnDef)
        {
            return columnDef.StartsWith(column + " ") || columnDef.StartsWith(_dialect.Quote(column));
        }
    }
}