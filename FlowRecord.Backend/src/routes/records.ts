import { Router, Request, Response } from 'express';
import pool from '../database/db';

const router = Router();

// 記録を保存
router.post('/', async (req: Request, res: Response) => {
    try {
        const { pc_name, window_title, event_type, start_time, end_time } = req.body;

        // バリデーション
        if (!pc_name || !window_title || !event_type || !start_time) {
            return res.status(400).json({
                error: 'Missing required fields: pc_name, window_title, event_type, start_time'
            });
        }

        // duration_secondsを計算（end_timeがある場合）
        let durationSeconds = null;
        if (end_time) {
            const start = new Date(start_time);
            const end = new Date(end_time);
            durationSeconds = Math.floor((end.getTime() - start.getTime()) / 1000);
        }

        const query = `
      INSERT INTO records (pc_name, window_title, event_type, start_time, end_time, duration_seconds)
      VALUES ($1, $2, $3, $4, $5, $6)
      RETURNING id
    `;

        const values = [pc_name, window_title, event_type, start_time, end_time, durationSeconds];
        const result = await pool.query(query, values);

        console.log(`✓ Record saved: ${event_type} - ${window_title}`);

        res.status(201).json({
            success: true,
            id: result.rows[0].id,
            message: 'Record saved successfully'
        });

    } catch (error) {
        console.error('Error saving record:', error);
        res.status(500).json({ error: 'Internal server error' });
    }
});

// 記録を取得（日付範囲指定可能）
router.get('/', async (req: Request, res: Response) => {
    try {
        const { pc_name, start_date, end_date, limit = '100' } = req.query;

        let query = 'SELECT * FROM records WHERE 1=1';
        const values: any[] = [];
        let paramCount = 1;

        if (pc_name) {
            query += ` AND pc_name = $${paramCount}`;
            values.push(pc_name);
            paramCount++;
        }

        if (start_date) {
            query += ` AND start_time >= $${paramCount}`;
            values.push(start_date);
            paramCount++;
        }

        if (end_date) {
            query += ` AND start_time <= $${paramCount}`;
            values.push(end_date);
            paramCount++;
        }

        query += ` ORDER BY start_time DESC LIMIT $${paramCount}`;
        values.push(parseInt(limit as string));

        const result = await pool.query(query, values);

        res.json({
            success: true,
            count: result.rows.length,
            records: result.rows
        });

    } catch (error) {
        console.error('Error fetching records:', error);
        res.status(500).json({ error: 'Internal server error' });
    }
});

// 統計情報を取得
router.get('/stats', async (req: Request, res: Response) => {
    try {
        const { pc_name, date } = req.query;

        let query = `
      SELECT 
        pc_name,
        DATE(start_time) as date,
        COUNT(*) as total_events,
        SUM(CASE WHEN event_type = 'window_open' THEN 1 ELSE 0 END) as window_opens,
        SUM(duration_seconds) as total_seconds
      FROM records
      WHERE 1=1
    `;

        const values: any[] = [];
        let paramCount = 1;

        if (pc_name) {
            query += ` AND pc_name = $${paramCount}`;
            values.push(pc_name);
            paramCount++;
        }

        if (date) {
            query += ` AND DATE(start_time) = $${paramCount}`;
            values.push(date);
            paramCount++;
        }

        query += ` GROUP BY pc_name, DATE(start_time) ORDER BY date DESC`;

        const result = await pool.query(query, values);

        res.json({
            success: true,
            stats: result.rows
        });

    } catch (error) {
        console.error('Error fetching stats:', error);
        res.status(500).json({ error: 'Internal server error' });
    }
});

export default router;