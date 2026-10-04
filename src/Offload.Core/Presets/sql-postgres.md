# SQL / PostgreSQL rules

## Style
- Use lowercase snake_case for all identifiers (tables, columns, functions)
- Always use explicit column lists in INSERT and SELECT statements
- Use explicit JOIN syntax (INNER, LEFT, etc.) rather than comma joins
- Use CTEs (WITH clauses) to improve readability of complex queries

## Schema
- Define a primary key on every table
- Define foreign keys with appropriate ON DELETE/UPDATE actions
- Use NOT NULL and CHECK constraints to enforce data integrity
- Use timestamptz for timestamp columns; use bigint identity or uuid for keys
- Create indexes on foreign keys and frequently filtered columns

## Queries
- Use parameterized queries exclusively to prevent SQL injection
- Run EXPLAIN (ANALYZE) before optimizing any slow query
- Use keyset pagination (WHERE id > last_seen) instead of OFFSET for large datasets
- Use EXISTS instead of IN for subqueries when checking for existence

## Safety
- Migrations must be additive and reversible
- Wrap multi-statement changes in transactions
- Use CREATE INDEX CONCURRENTLY on live tables to avoid locks

## Avoid
- Avoid SELECT *; specify columns explicitly
- Avoid applying functions to indexed columns in WHERE clauses
- Avoid storing monetary values in float types
- Avoid constructing SQL via string concatenation
