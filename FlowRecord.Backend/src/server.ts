import express from 'express';
import cors from 'cors';
import dotenv from 'dotenv';
import recordsRouter from './routes/records';
import pool from './database/db';

dotenv.config();

const app = express();
const PORT = process.env.PORT || 3000;

// ミドルウェア
app.use(cors());
app.use(express.json());

// ルート
app.use('/api/records', recordsRouter);

// ヘルスチェック
app.get('/health', (req, res) => {
    res.json({ status: 'ok', message: 'FlowRecord API is running' });
});

// ルートエンドポイント
app.get('/', (req, res) => {
    res.json({
        message: 'FlowRecord API',
        version: '1.0.0',
        endpoints: {
            health: '/health',
            records: '/api/records',
            stats: '/api/records/stats'
        }
    });
});

// データベース接続をテスト
async function testDatabaseConnection() {
    try {
        await pool.query('SELECT NOW()');
        console.log('✓ Database connection successful');
    } catch (error) {
        console.error('✗ Database connection failed:', error);
        process.exit(1);
    }
}

// サーバー起動
async function startServer() {
    await testDatabaseConnection();

    app.listen(PORT, () => {
        console.log(`\n🚀 FlowRecord API Server started`);
        console.log(`📡 Listening on http://localhost:${PORT}`);
        console.log(`📊 Health check: http://localhost:${PORT}/health\n`);
    });
}

startServer();