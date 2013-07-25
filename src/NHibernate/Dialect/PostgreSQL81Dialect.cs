using System;
using System.Data;
using NHibernate.SqlCommand;

namespace NHibernate.Dialect
{
	/// <summary>
	/// An SQL dialect for PostgreSQL 8.1 and above.
	/// </summary>
	/// <remarks>
	/// <para>
	/// PostgreSQL 8.1 supports <c>FOR UPDATE ... NOWAIT</c> syntax.
	/// </para>
	/// <para>
	/// PostgreSQL supports Identity column using the "SERIAL" type.
	/// Serial type is a "virtual" type that will automatically:
	/// </para>
	/// <list type="bullet">
	/// <item><description>Create a sequence named tablename_colname_seq.</description></item>
	/// <item><description>Set the default value of this column to the next value of the 
	/// sequence. (using function <c>nextval('tablename_colname_seq')</c>)</description></item>
	/// <item><description>Add a "NOT NULL" constraint to this column.</description></item>
	/// <item><description>Set the sequence as "owned by" the table.</description></item>
	/// </list>
	/// <para>
	/// To insert the next value of the sequence into the serial column,
	/// exclude the column from the list of columns 
	/// in the INSERT statement or use the DEFAULT key word.
	/// </para>
	/// <para>
	/// If the table or the column is dropped, the sequence is dropped too.
	/// </para>
	/// </remarks>
	/// <seealso cref="PostgreSQLDialect" />
	public class PostgreSQL81Dialect : PostgreSQLDialect
	{
		public override string ForUpdateNowaitString
		{
			get { return " for update nowait"; }
		}

		public override string GetForUpdateNowaitString(string aliases)
		{
			return " for update of " + aliases + " nowait";
		}

		/// <summary>
		/// PostgreSQL supports Identity column using the "SERIAL" type.
		/// </summary>
		public override bool SupportsIdentityColumns
		{
			get { return true; }
		}

		/// <summary>
		/// PostgreSQL doesn't have type in identity column.
		/// </summary>
		/// <remarks>
		/// To create an identity column it uses the SQL syntax
		/// <c>CREATE TABLE tablename (colname SERIAL);</c> or 
		/// <c>CREATE TABLE tablename (colname BIGSERIAL);</c>
		/// </remarks>
		public override bool HasDataTypeInIdentityColumn
		{
			get { return false; }
		}

		/// <summary>
		/// PostgreSQL supports <c>serial</c> and <c>serial4</c> type for 4 bytes integer auto increment column.
		/// <c>bigserial</c> or <c>serial8</c> can be used for 8 bytes integer auto increment column.
		/// </summary>
		/// <returns><c>bigserial</c> if <paramref name="type"/> equal Int64,
		/// <c>serial</c> otherwise</returns>
		public override string GetIdentityColumnString(DbType type)
		{
			return (type == DbType.Int64) ? "bigserial" : "serial";
		}

		/// <summary>
		/// The sql syntax to insert a row without specifying any column in PostgreSQL is
		/// <c>INSERT INTO table DEFAULT VALUES;</c>
		/// </summary>
		public override string NoColumnsInsertString
		{
			get { return "default values"; }
		}

		/// <summary> 
		/// Get the select command to use to retrieve the last generated IDENTITY
		/// value for a particular table 
		/// </summary>
		/// <param name="tableName">The table into which the insert was done </param>
		/// <param name="identityColumn">The PK column. </param>
		/// <param name="type">The <see cref="DbType"/> type code. </param>
		/// <returns> The appropriate select command </returns>
		public override string GetIdentitySelectString(string identityColumn, string tableName, DbType type)
		{
			return "select currval('" + tableName + '_' + identityColumn + "_seq')";
		}

		public override SqlString AppendIdentitySelectToInsert(SqlString insertSql)
		{
			return insertSql.Append("; " + IdentitySelectString);
		}

		public override bool SupportsInsertSelectIdentity
		{
			get { return true; }
		}
	}
}
